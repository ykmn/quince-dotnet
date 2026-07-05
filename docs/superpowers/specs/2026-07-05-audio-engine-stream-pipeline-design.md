# Аудио-движок: пайплайн для stream-каналов (v1)

**Дата:** 2026-07-05
**Статус:** согласован с пользователем, ожидает написания плана реализации.

## Область охвата

Первый полноценный срез аудио-движка Quince.Service — целиком для каналов
с `source.type: stream` (Icecast/HLS), поскольку подавляющее большинство
реальных конфигов (`config/`, `config/RP/`, ~150 файлов) используют именно
stream-источник.

Включено: захват потока, запись в файл с ротацией/удержанием, метрирование
уровня (True Peak + LUFS, EBU R128), детектор тишины, реконнект при обрыве,
логирование всех требуемых событий, обвязка UI (кнопка Старт/Стоп, статус-точка,
живой индикатор уровня).

**Не входит в этот инкремент** (последующие итерации):
- захват со звуковой карты (`source.type: soundcard`);
- чтение метаданных потока (ICY/HLS — название трека) и `MetadataWriter`;
- кнопки редактирования/клонирования/удаления канала;
- отдельное окно индикаторов (▦ «Индикаторы»).

## Источник портирования

Прямой порт легаси Python-реализации из `../quince/src/audio/`:
`capture_stream.py`, `writer.py`, `meter.py`, `silence.py`, `channel_engine.py`
(только stream-половина). Схема конфига (`ChannelConfig.cs`) уже соответствует
легаси-схеме — маппинг полей не требуется.

## Архитектура

```
StreamCapture (ffmpeg-подпроцесс, декодирование → PCM f32le)
   │  fan-out через отдельный Channel<float[]> на подписчика
   ├──► AudioWriter    (ffmpeg-подпроцесс, PCM → mp3/aac/wav, ротация по временной сетке + retention)
   ├──► LevelMeter     (True Peak + LUFS M/S/I, колбэк ~10 Гц)
   └──► SilenceDetector (RMS + гистерезис state machine)

ChannelEngine        — владеет четырьмя компонентами выше, связывает колбэки, Start()/Stop()/Status
AudioEngineManager   — новый singleton IHostedService рядом с ChannelManager;
                        владеет одним ChannelEngine на каждый загруженный канал,
                        авто-старт для auto_start: true, Start/Stop для UI
```

- **FFmpeg-процессы**: `System.Diagnostics.Process`, stdout — для захвата,
  stdin — для записи. Те же две роли подпроцесса, что и в Python-версии
  (декодирование потока → PCM, PCM → файл); без пути через `sounddevice`.
- **Fan-out**: `System.Threading.Channels.Channel<T>` на подписчика (bounded,
  drop-oldest при переполнении) вместо `queue.Queue` — та же семантика
  (writer/meter/silence — у каждого своя очередь, один producer-поток читает
  stdout ffmpeg).
- **DSP-математика** (биквады K-weighting, oversampling для true peak,
  gating для integrated loudness): прямой порт чистой numpy-fallback ветки
  уже присутствующей в `meter.py` (без scipy) на обычные C#-массивы — новый
  NuGet-пакет не нужен.

## Компоненты

### StreamCapture
Порт `capture_stream.py`. Строит ту же команду ffmpeg (`-user_agent`,
`-tls_verify 0` при `allow_invalid_ssl`, `-allowed_extensions ALL` для HLS,
`-map 0:a:{hls_bitrate_index}`, вывод `pcm_f32le`/44100/stereo в `pipe:1`).
Фоновый Task читает stdout блоками фиксированного размера, преобразует во
фреймы, раздаёт подписчикам. Цикл реконнекта с тем же enum статуса
(`Stopped/Connecting/Streaming/Reconnecting/Error`) и счётчиком
`ReconnectAttempt`, задержка — из `source.reconnect_delay_seconds`.

### AudioWriter
Порт `writer.py`. Команда ffmpeg строится по `output_format` (wav/mp3/aac,
`mode: custom` — ресемплинг). Ротация по временной сетке через
`ComputeNextBoundary` (выравнивание от полуночи, `file_duration_seconds`),
папка по дате (`date_folder_format`) + имя файла (`file_name_format`) —
токены `YYYY/MM/DD`, `hh/mm/ss`. Cooldown 5 c при падении ffmpeg сразу после
открытия. Очистка старых файлов (`retention_days`) при старте и при смене даты.

### LevelMeter
Порт `meter.py`: каскад биквадов K-weighting (EBU R128), True Peak через
4×-передискретизацию линейной интерполяцией (numpy-fallback ветка — в .NET
нет scipy, так что это как раз более простая из двух веток для портирования),
LUFS momentary/short-term/integrated с тем же гейтингом (-70 LUFS абсолютный,
-10 LU относительный). Колбэк `LevelReading` ~10 Гц, с той же защитой от
завала апдейтами при всплесках HLS-данных (wall-clock pacing).

### SilenceDetector
Почти дословный порт `silence.py`: RMS в dBFS, состояния `SOUND`/`SILENT` с
гистерезисом `trigger_seconds`/`resume_seconds`.

### ChannelEngine
Порт stream-половины `channel_engine.py`: собирает capture → writer/meter/
silence, отслеживает попытки реконнекта таймером, транслирует колбэки
тишины/реконнекта в `EngineStatus` (`IsRecording`, `ReconnectAttempt`,
`IsSilent`), предоставляет `Start()`/`Stop()`/`UpdateConfig()` (перезапуск
только если изменились поля, влияющие на пайплайн — аналог `_pipeline_changed`).

## Оркестрация и обвязка UI

**AudioEngineManager** (новый singleton `IHostedService`, рядом с
`ChannelManager`): в `StartAsync` для каждого загруженного канала с
`Source.Type == "stream"` и `AutoStart == true` создаёт `ChannelEngine` и
вызывает `Start()`. Хранит `Dictionary<string, ChannelEngine>` по имени
канала — доступно для UI. В `StopAsync` останавливает все работающие
движки (graceful shutdown ffmpeg), чтобы остановка/рестарт службы не
оставляла осиротевшие процессы ffmpeg.

**Кнопка Старт/Стоп** (`ChannelCard.razor`): становится реальным тумблером —
инжектит `AudioEngineManager`, клик вызывает `Start(channel)`/`Stop(channel)`,
подпись переключается Старт/Стоп. Для каналов `soundcard` кнопка остаётся
видимой, но задизейблена с тултипом («звуковая карта пока не реализована»),
чтобы граница инкремента была видна в самом UI, а не только в документации.

**Статус-точка**: привязана к реальному `EngineStatus` — серая (остановлен) /
зелёная (стрим идёт) / жёлтая (реконнект, пульсирует с `ReconnectAttempt`) /
красная (ошибка). Вторая точка (только для stream) отражает `IsSilent`, если
`silence_detector.enabled`.

**Живой индикатор уровня**: `ChannelEngine` пушит `LevelReading` через
`IHubContext<LevelHub>` в SignalR-группу канала (~10 Гц, совпадает с
собственным троттлингом метра — доп. буферизация не нужна). `ChannelCard`
подписывается в `OnInitializedAsync` через `HubConnection`, обновляет
бар/значение TP; отписывается в `DisposeAsync`. Запись продолжается
независимо от того, открыта ли страница в браузере — хаб — это чисто
UI push-канал, не часть жизненного цикла движка.

## Логирование и конфигурация

Все события, требуемые [[quince_dotnet_logging_spec]], в этом инкременте
подключаются к реальному коду (сейчас реальны только события загрузки
каналов и старта приложения):
- старт/стоп канала → `ChannelEngine.Start/Stop`;
- ротация файла → `AudioWriter` (открытие/закрытие/ротация);
- обрыв/восстановление потока → переходы статуса `StreamCapture`;
- ошибки записи → сбой записи в stdin ffmpeg, ffmpeg не найден, ненулевой
  код выхода;
- срабатывание детектора тишины → переходы состояния `SilenceDetector`;
- очистка старых логов — уже реализовано, не затрагивается.

Всё — через `ILogger<T>.BeginScope(new Dictionary<string,object>{["Channel"]=name})`,
по уже используемому в `ChannelManager` паттерну, так что строки попадают в
`[channel_name]` по существующему формату `FileLogger` — изменений в
инфраструктуре логирования не требуется.

**Конфигурация**: добавить `FfmpegPath` в `appsettings.json` (по умолчанию
`"tools\ffmpeg.exe"`, резолвится через существующий `PathResolver`/
`AppContext.BaseDirectory` — та же схема анкеринга, что у `ConfigDir`/`LogDir`).
В `Quince.Service.csproj` — `<Content Include="tools\**" CopyToOutputDirectory="PreserveNewest" />`.
Бинарники `ffmpeg.exe`/`ffprobe.exe` (официальная статическая сборка для
Windows, gyan.dev — дистрибьютор, на который ссылается сам проект FFmpeg)
будут получены и добавлены в `Quince.Service/tools/` на этапе реализации.

## Тестирование и результаты

- Юнит-тесты (xUnit, новый проект `Quince.Service.Tests`) для чисто
  логических частей: расчёт пути/границ ротации в `AudioWriter`, математика
  K-weighting/true-peak/LUFS в `LevelMeter` на известных фикстурах,
  state machine `SilenceDetector`.
- Ручная сквозная проверка: указать реальный конфиг канала на живой
  Icecast/HLS URL из `config/RP/`, нажать Старт в UI, убедиться что
  ротированный файл появляется в `save_path`, что бар уровня двигается,
  что строки лога появляются в `log/YYYY-MM-DD.log` в требуемом формате.
- Согласно [[quince_dotnet_process_conventions]]: увеличить патч-версию,
  обновить `HISTORY.md`/`CHANGELOG.md`/`README.md` (новая формулировка
  «известных ограничений» — звуковая карта и метаданные всё ещё не
  реализованы), опубликовать новую сборку в `release/`.

## Открытые решения, зафиксированные с пользователем

| Вопрос | Решение |
|---|---|
| Порядок инкрементов | Полный пайплайн (capture+writer+meter+silence) для одного типа источника, не по подсистемам |
| Тип источника первым | Stream (Icecast/HLS) — большинство реальных конфигов |
| Декодирование/кодирование | FFmpeg-подпроцесс (как в легаси), не BASS-native |
| Откуда ffmpeg.exe | Получить официальную статическую сборку и забандлить в `release/` |
