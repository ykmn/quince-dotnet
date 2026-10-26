Теперь у меня достаточно материала для полноценного code review. Вот мой анализ:

---

# Code Review: Quince Audio Logger

## Общая оценка

Проект написан очень качественно. Видно, что автор хорошо разбирается в .NET async/await, threading, Blazor Server и в предметной области (аудио, broadcast). Код хорошо задокументирован, решения обоснованы inline-комментариями.

---

## Архитектурные решения

**Плюсы:**
- Pipeline через `IAudioCapture → PlayoutBuffer → LevelMeter / AudioWriter / SilenceDetector` — чистое разделение ответственности
- Pub/sub через `C# events` в `AudioEngineManager` — понятная модель для real-time UI updates
- Opt-in auth (файл `ldap.yaml` = вкл/выкл) — изящное решение без кода-переключателя
- `PlayoutBuffer` с `BoundedChannelFullMode.DropOldest` — правильное решение проблемы компаундного лага

---

## Проблемы и замечания

### P1 — Критические / потенциальные баги

**1. `SilenceDetector` использует константу `ChunkDurationApproxSeconds = 0.1` вместо реального времени**

`SilenceDetector.cs:9,86,109`

Таймеры `_silenceTimer` и `_soundTimer` инкрементируются на фиксированную константу `0.1` за каждый chunk, независимо от реального размера chunk'а. Если ffmpeg выдаёт chunk'и другого размера (например, при bitrate/samplerate изменении), то реальные пороги тишины будут неверными. `AudioChunk` уже содержит `FrameCount` — логично считать реальную длительность:

```csharp
// Текущий код:
_silenceTimer += ChunkDurationApproxSeconds;

// Правильнее:
_silenceTimer += (double)chunk.Samples.Length / (_channels * _sampleRate);
```

Для этого `SilenceDetector` нужно передать `sampleRate` и `channels` (как уже сделано в `LevelMeter`).

---

**2. `ChannelEngine.Stop()` ждёт `_monitorTask` максимум 2 секунды, но не логирует таймаут**

`ChannelEngine.cs:231`

```csharp
try { _monitorTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
```

`AggregateException` перехватывается молча. Если задача не завершилась (например, завершилась по таймауту), об этом никто не узнает. Стоит логировать хотя бы на уровне `LogDebug`.

---

**3. `AudioWriter.Stop()` не ждёт `_task` корректно при `OperationCanceledException`**

`AudioWriter.cs:92-98`

```csharp
_cts?.Cancel();
try { _task?.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
```

`AggregateException` проглатывается без анализа — в том числе если `Wait` вернулся по таймауту. В таком случае `CloseProc()` ниже может вызваться параллельно с ещё живым `RunAsync`, что приведёт к `_proc.Dispose()` пока в него ещё пишут. Следует проверять `_task.IsCompleted` после `Wait`.

---

**4. `AuthService.GetSession` вызывает `SavePersistedSessions()` при каждом истёкшем токене**

`AuthService.cs:232-238`

Каждый запрос с истёкшей сессией вызывает `SavePersistedSessions()`, которая держит `_sessionsFileLock` и записывает весь файл на диск. При большом количестве параллельных запросов (или при атаке с устаревшими токенами) это может стать узким местом. Можно отложить запись до явного `DeleteSession` или батчить.

---

### P2 — Качество кода / проектные решения

**5. `PlayoutBuffer._queuedSeconds` накапливается вечно**

`PlayoutBuffer.cs:161,78`

`_queuedSeconds` используется только для определения момента прогрева (`justPrimed`). После прогрева он продолжает расти бесконечно, но никогда больше не читается. Это не баг, но потенциально запутывает при отладке — либо стоит оставить `_queuedSeconds = 0` после прогрева, либо добавить комментарий.

---

**6. `ChannelManager.Channels` создаёт копию списка при каждом обращении**

`ChannelManager.cs:37`

```csharp
get { lock (_lock) { return _channels.ToList(); } }
```

Если `Channels` опрашивается часто (каждые 500ms из `ChannelEngine.MonitorAsync`, плюс Blazor renders), это создаёт постоянное давление на GC. Более экономно: вернуть `ImmutableList` или использовать `_channels.AsReadOnly()` при условии, что список не меняется снаружи lock'а (что гарантируется архитектурой, но не типом).

---

**7. `AudioEngineManager.Start()` держит lock на весь `engine.Start()`**

`AudioEngineManager.cs:170-215`

```csharp
lock (_lock)
{
    // ...
    engine.Start(suppressRecording); // долгая операция
}
```

`engine.Start()` создаёт процессы, подписывается на каналы — это потенциально медленная операция. Держать глобальный `_lock` всё это время блокирует любые другие операции с `_engines` (включая приходящие с других каналов события `StatusUpdated`). Рассмотреть создание движка вне lock, и только финальную запись в `_engines` — под lock.

---

**8. `LoginLockoutTracker.RecordFailure` — потенциальный TOCTOU для IP-блокировки**

`LoginLockoutTracker.cs:106-111`

```csharp
lock (ipState) { events = ++ipState.LockoutEvents; }

if (events < Math.Max(1, cfg.MaxLockoutCycles)) return;

lock (ipState) { ipState.BlockedUntil = ... }
```

Между двумя lock-блоками другой поток может тоже прочитать `events >= threshold` и оба запишут `BlockedUntil` — время окончания блокировки удвоится (второй вызов перезапишет `BlockedUntil` на `UtcNow + IpLockoutSeconds`, как будто таймер сбросился). Это безвредно в смысле безопасности (IP в любом случае заблокируется), но `BlockedUntil` будет немного позже ожидаемого. Исправляется объединением обоих lock-блоков в один.

---

**9. `SilenceDetector.IsSilent` читается вне синхронизации**

`SilenceDetector.cs:35`

```csharp
public bool IsSilent => _state == "SILENT";
```

`_state` — обычная строка `string`, изменяется из `RunAsync` (фоновый task). `IsSilent` читается из Blazor-компонентов или `MonitorAsync` на других потоках. В C# присвоение ссылочного типа атомарно на x64, поэтому разрыва не будет, но `volatile` или `Interlocked` всё же стоит использовать для ясности намерения.

---

**10. `HlsMetadataReader.Stop()` ждёт 15 секунд**

`HlsMetadataReader.cs:76`

```csharp
try { _task?.Wait(TimeSpan.FromSeconds(15)); } catch (AggregateException) { }
```

15 секунд — значительно дольше, чем у других компонентов (2-10 сек). При остановке канала с несколькими активными metadata reader'ами это может задержать весь `ChannelEngine.Stop()`. Объясняется ли это конкретным сценарием из HISTORY.md? Если нет — стоит унифицировать.

---

### P3 — Стиль и мелочи

**11. Дублирование кода в `LdapAuthenticator.AuthenticateAgainstDomain`**

`LdapAuthenticator.cs:48-132`

Два ветвления (`with secret` / `without secret`) содержат дублирующийся код вызова `SearchUser` и `ResolveGroups`. Разница только в порядке (с secret — сначала search, потом bind; без — наоборот). Логику можно структурировать через локальный метод.

---

**12. Magic string `"SOUND"` / `"SILENT"` в `SilenceDetector`**

`SilenceDetector.cs:18,35,82,88`

Состояние `_state` — строка вместо `enum`. Простая замена на `enum DetectorState { Sound, Silent }` исключит опечатки и улучшит навигацию.

---

**13. `YamlConfigLoader` — статические десериализаторы как static readonly**

`YamlConfigLoader.cs:10-17`

`_deserializer` и `_serializer` — `static readonly`, что правильно. Однако они не логируют ошибки (logger передаётся в instance, но не имеет доступа к static-полям). Это уже учтено через `LogLoadError`, но если в будущем кто-то перенесёт логику — связь неочевидна.

---

**14. `Program.cs` — `Secure = false` в cookie options захардкожено**

`Program.cs:190`

```csharp
Secure = false, // this app runs over plain HTTP on the LAN today
```

Комментарий объясняет решение, но если приложение когда-либо окажется за HTTPS-прокси (nginx, IIS с TLS), сессионный cookie не будет помечен Secure. Стоит добавить `SameSite = SameSiteMode.Strict` или параметризовать через конфиг.

---

## Тесты

Покрытие выглядит хорошо: 30 тест-файлов, включая unit-тесты для DSP-алгоритмов (`TruePeakCalculator`, `LufsCalculator`, `KWeightingFilter`), парсеров и auth. Видно, что критические алгоритмы покрыты.

**Отсутствует:**
- Интеграционные тесты для `ChannelEngine` (пайплайн целиком)
- Тест race condition для `AudioEngineManager.Start` под lock (п.7 выше)
- Тест TOCTOU для `LoginLockoutTracker` (п.8)

---

## Итог

| Категория | Оценка |
|---|---|
| Архитектура | ★★★★★ |
| Корректность concurrency | ★★★★☆ |
| Документированность | ★★★★★ |
| Тестирование | ★★★★☆ |
| Стиль кода | ★★★★☆ |

Наиболее важные пункты к исправлению: **#1** (SilenceDetector таймер), **#3** (AudioWriter race при Stop), **#7** (долгий lock в AudioEngineManager.Start).


---

# Статус по состоянию на 2026-10-26 (v1.00.074)

Все 14 замечаний перепроверены по текущему коду — актуальны были все.

| # | Замечание | Статус |
|---|---|---|
| 1 | `SilenceDetector`: фиксированные 0,1 с на chunk | Исправлено: длительность считается по сэмплам/каналам/частоте |
| 2 | `ChannelEngine.Stop()` молча теряет таймаут | Исправлено: `StopWait` пишет предупреждение |
| 3 | `AudioWriter.Stop()` не проверяет завершение задачи | Частично: таймаут теперь виден в логе; `CloseProc()` по-прежнему вызывается после него, чтобы не оставить ffmpeg-процесс |
| 4 | `GetSession` пишет файл на каждый просроченный токен | Исправлено: удаление только из памяти |
| 5 | `_queuedSeconds` растёт после прогрева | Исправлено |
| 6 | `ChannelManager.Channels` копирует список при каждом чтении | Исправлено: кешированный снимок |
| 7 | `AudioEngineManager.Start` держит общий lock | **Не сделано.** Lock защищает от утечки процесса при остановке (см. комментарии в `StopAsync`); нужна отдельная переработка. Усилилось после появления мониторинга Zabbix: `GetMonitoringState` каждые 5 с ждёт тот же lock |
| 8 | TOCTOU в `LoginLockoutTracker` | Исправлено: один `lock` |
| 9 | `SilenceDetector.IsSilent` без синхронизации | Исправлено: `volatile enum` |
| 10 | Ожидание 15 с в metadata-читателях | Не менялось (HLS/Icecast читатели делают сетевые запросы с таймаутами); таймаут теперь виден в логе |
| 11 | Дублирование в `LdapAuthenticator` | Не сделано: нет тестов на LDAP, риск регрессии выше пользы |
| 12 | Строки `"SOUND"`/`"SILENT"` | Исправлено: `enum` |
| 13 | Статические десериализаторы | Без изменений: замечание информационное |
| 14 | `Secure = false` у cookie | Не сделано: `SameSite` уже `Lax`; нужен параметр в `settings.yaml` при переходе на HTTPS |

# Дополнения к review (по логам рабочего сервера 16–26.10.2026)

**D1. Реконнект без backoff** (исправлено в 1.00.073). Фиксированные 3 с и неограниченное число попыток: 20.10 — 6666 попыток за сутки, лог 40 МБ, 22 ч простоя четырёх каналов.

**D2. Шум в логах** (исправлено в 1.00.073). Стек `HttpRequestException` на каждый опрос JSON-метаданных (~21 тыс. записей за 10 дней), строка на каждую попытку реконнекта.

**D3. Нет алерта на «канал долго переподключается»** (исправлено в 1.00.073, тревога в Zabbix). При `reconnect_max_attempts: 0` канал мог часами не писать без единой тревоги.

**D4. Коды выхода FFmpeg в логе нечитаемы.** `FFmpeg завершился с кодом -875574520` — это не код выхода, а HRESULT/FourCC-значение; для диагностики полезнее hex и расшифровка. Не сделано.

**D5. Повторяющийся паттерн `try { Wait } catch (AggregateException) { }`** в 8 классах (п. 2, 3, 10 — частные случаи). Заменён на `StopWait`.

**D6. Окончания строк.** Рабочая копия в CRLF, индекс в LF (`core.autocrlf=true`, `.gitattributes` нет); скрипты, перезаписывающие файлы, легко меняют окончания целиком. Рекомендация: добавить `.gitattributes` (`* text=auto`).

**D7. `docs/HISTORY.md`: потерянные обратные слеши.** В записях 171–174 `release.00.0NN` превратилось в управляющий символ `` (исправлено в 1.00.073). Причина — экранирование при записи; при добавлении записей скриптом проверять пути с `\`.

**D8. Нестабильный тест.** `YamlConfigLoaderTests.LoadAll_LegacyFileDurationSeconds_MigratesToMinutes` изредка падает в полном прогоне (`Sequence contains no elements`), отдельно проходит всегда. Причина не найдена, к изменениям v1.00.073/074 не относится.
