# Айва (Quince.Service)

Многоканальный аудиологгер — веб-приложение на ASP.NET Core (Blazor Server), устанавливаемое как служба Windows. Версия: см. `CHANGELOG.md` (текущая — `0.00.002`).

## Требования

- .NET 8 SDK
- Windows (проект целится на `net8.0-windows`; аудио-движок использует бандлированный `ffmpeg.exe` из `Quince.Service/tools/`)

## Запуск в разработке

Из папки репозитория:

```
dotnet run --project .\Quince.Service\
```

или из папки `Quince.Service\`:

```
dotnet run
```

Оба варианта равнозначны: рабочая директория запущенного процесса при `dotnet run` — это всегда папка проекта, а `config/`/`log/` резолвятся относительно папки скомпилированного приложения (см. ниже), а не от того, откуда вызван `dotnet`.

Приложение слушает `http://localhost:5000` (см. `appsettings.json`, ключ `Urls`).

## Сборка и публикация

```
dotnet build .\Quince.Service\
dotnet publish .\Quince.Service\ -c Release -o .\release\<версия>
```

## Расположение данных

Все рабочие данные приложения лежат **рядом со скомпилированным `Quince.Service.exe`** (не зависят от того, как и откуда запущен процесс — важно для установки как службы Windows, где рабочая директория обычно не совпадает с папкой приложения):

- `config/` — YAML-конфиги каналов и `app.yaml` (настройки приложения). Путь настраивается ключом `ConfigDir` в `appsettings.json` (по умолчанию `"config"`).
- `log/` — файлы журнала, по одному на день. Путь настраивается ключом `LogDir` в `appsettings.json` (по умолчанию `"log"`).
- `recording/` — записанные аудиофайлы по каналам (создаётся движком записи; на момент версии `0.00.002` реализовано для каналов `source.type: stream` — для `soundcard` движок ещё не реализован).

Оба пути (`config`, `log`) можно также задать абсолютными — тогда они не привязываются к папке приложения.

## Конфигурация канала (`config/*.yaml`)

Пример (см. реальные примеры в `Quince.Service/config/`):

```yaml
name: Ретро FM HLS
source:
  type: stream            # stream | soundcard
  url: https://.../playlist.m3u8
  stream_type: hls         # hls | icecast | icecast_mp3
  device_name: ''          # для type: soundcard
  device_index: -1
  device_uid: ''
  allow_http: false
  allow_invalid_ssl: false
  metadata_url: https://.../metadata.json
  reconnect_delay_seconds: 3
input_format:
  sample_rate: 0
  bit_depth: 0
  channels: 0
  bitrate: 0
  codec: ''
output_format:
  mode: original
  file_format: aac          # mp3 | aac | wav ...
  sample_rate: 48000
  bit_depth: 16
  channels: 2
  bitrate_kbps: 96
save_path: D:\recording\Retro
date_folder_format: YYYY-MM-DD
file_name_format: hh-mm-ss
file_duration_seconds: 600
record_audio: true
retention_days: 30
auto_start: false
silence_detector:
  enabled: false
  threshold_dbfs: -60.0
  trigger_seconds: 3.0
  resume_seconds: 1.0
metadata_path: ''
```

Файлы без поля `name` (например `app.yaml`) не считаются каналами и не отображаются в списке.

## Настройки приложения (`config/app.yaml`)

```yaml
log_level: INFO             # DEBUG | INFO | WARNING | ERROR
log_retention_days: 30
meter_colors:
  zone_yellow_db: -12.0
  zone_red_db: -3.0
  color_green: '#1d761d'
  color_yellow: '#ccaa00'
  color_red: '#cc1818'
```

## Журнал (`log/`)

- Новый файл каждый день: `log/YYYY-MM-DD.log`.
- Уровень логирования настраивается через `log_level` в `app.yaml` (DEBUG/INFO/WARNING/ERROR).
- Старые файлы удаляются автоматически по истечении `log_retention_days` дней (по умолчанию 30).
- Формат строки: `YYYY-MM-DD HH:MM:SS.mmm [LEVEL] [channel_name] сообщение` — `[channel_name]` заменяется на `[-]` для сообщений уровня приложения, не привязанных к конкретному каналу.

## Известные ограничения (по состоянию на 0.00.002)

- Захват со звуковой карты (`source.type: soundcard`) и метаданные потока (ICY/HLS — название трека) ещё не реализованы. Кнопка Старт/Стоп для таких каналов задизейблена.
- Окно индикаторов (▦) и редактирование/клонирование/удаление канала (✎/⧉/✕) остаются визуальными заглушками, без реального действия.
- Папка `config/RP/` (~150 доп. станций) не читается рекурсивно — обрабатываются только файлы верхнего уровня `config/`.

## Структура проекта

```
Quince.Service/
  Configuration/     — модели и загрузчики YAML-конфигов (ChannelConfig, AppConfig)
  Services/          — ChannelManager, файловый логгер, форматирование для UI
  Pages/             — Blazor-страницы и layout (топбар, бургер-меню, карточки каналов)
  config/            — YAML-конфиги (копируются в выходную папку сборки)
  wwwroot/           — статика: стили, шрифты, иконка
```
