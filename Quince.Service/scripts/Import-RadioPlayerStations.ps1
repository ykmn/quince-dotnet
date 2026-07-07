<#
.SYNOPSIS
    Обновить MOSCOW.md и создать YAML-конфиги станций radioplayer.ru (Windows PowerShell).

.DESCRIPTION
    Аналог import_moscow_stations.py для Windows.
    Загружает список московских радиостанций с radioplayer.ru API,
    записывает MOSCOW.md и опционально создаёт YAML-конфиги для Айва.

.EXAMPLE
    # Только обновить MOSCOW.md
    .\scripts\Import-RadioPlayerStations.ps1

    # Создать YAML-конфиги в ./config/
    .\scripts\Import-RadioPlayerStations.ps1 -CreateConfigs

    # Указать другую папку конфигов
    .\scripts\Import-RadioPlayerStations.ps1 -CreateConfigs -ConfigDir "D:\quince\config"

.PARAMETER CreateConfigs
    Создать YAML-конфиги для всех найденных станций с потоком.

.PARAMETER ConfigDir
    Папка для сохранения YAML-конфигов. По умолчанию: ./config/ относительно корня проекта.

.PARAMETER MoscowMd
    Путь к MOSCOW.md. По умолчанию: ./MOSCOW.md относительно корня проекта.

.PARAMETER Filter
    Фильтр по названию станции (подстрока, без учёта регистра). * = все станции.
    Пример: -Filter "Ретро FM"  или  -Filter "Москва"
#>

#Requires -Version 5.1

param(
    [switch]$CreateConfigs,
    [string]$ConfigDir = "",
    [string]$MoscowMd  = "",
    [string]$Filter    = "*"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptRoot  = Split-Path -Parent $MyInvocation.MyCommand.Path
#$ProjectRoot = Split-Path -Parent $ScriptRoot
$ProjectRoot = $ScriptRoot

if (-not $ConfigDir) { $ConfigDir = Join-Path $ProjectRoot "config" }
if (-not $MoscowMd)  { $MoscowMd  = Join-Path $ProjectRoot "MOSCOW.md" }

$GtsUrl      = "https://api.radioplayer.ru/api/web/site/gts"
$StationsUrl = "https://api.radioplayer.ru/api/web/site/stations?region=1"
$UserAgent   = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"
$TimeoutSec  = 15

# ── safe property access (StrictMode: PSCustomObjects from JSON may lack keys) ─

function Get-Prop {
    param([object]$Object, [string]$Name, $Default = "")
    if ($null -eq $Object) { return $Default }
    $prop = $Object.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value } else { return $Default }
}

# ── HTTP helper ──────────────────────────────────────────────────────────────

function Invoke-Api {
    param(
        [string]$Url,
        [string]$Token = ""
    )
    $headers = @{ "User-Agent" = $UserAgent }
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }

    Invoke-RestMethod `
        -Uri        $Url `
        -Headers    $headers `
        -TimeoutSec $TimeoutSec `
        -UseBasicParsing
}

# ── GTS token + station list ─────────────────────────────────────────────────

function Get-Stations {
    Write-Host "Получение GTS-токена…"
    $gts   = Invoke-Api -Url $GtsUrl
    $token = Get-Prop $gts "st"
    if (-not $token) { $token = Get-Prop $gts "token" }
    if (-not $token) { throw "Не удалось получить GTS-токен" }

    Write-Host "Загрузка списка станций (регион: Москва)…"
    $data = Invoke-Api -Url $StationsUrl -Token $token

    if ($data -is [System.Collections.IEnumerable] -and $data -isnot [string]) {
        return @($data)
    } elseif ($data.data) {
        return @($data.data)
    } else {
        throw "Неожиданный формат ответа API"
    }
}

# ── stream extraction ────────────────────────────────────────────────────────

function Get-StreamInfo {
    param([object]$Station)

    $streamUrl  = ""
    $metaUrl    = ""
    $streamType = "icecast"

    $source = Get-Prop $Station "source"
    if ($source) {
        $streamUrl  = $source
        $streamType = if ($source -match "\.m3u8") { "hls" } else { "icecast" }
    }

    $mobileSource = Get-Prop $Station "mobileSourceUrl"
    if (-not $streamUrl -and $mobileSource) {
        $streamUrl  = $mobileSource
        $streamType = if ($mobileSource -match "\.m3u8") { "hls" } else { "icecast" }
    }

    $metaObj = Get-Prop $Station "metadata" $null
    if ($metaObj) {
        $jsonUrl         = Get-Prop $metaObj "jsonUrl"
        $uniqueIdJsonUrl = Get-Prop $metaObj "uniqueIdJsonUrl"
        $metaUrl = if ($jsonUrl) { $jsonUrl } elseif ($uniqueIdJsonUrl) { $uniqueIdJsonUrl } else { "" }
    }

    if (-not $metaUrl) {
        foreach ($key in @("metadataUrl", "metadata_url", "nowPlayingUrl")) {
            $v = Get-Prop $Station $key
            if ($v) { $metaUrl = $v; break }
        }
    }

    [PSCustomObject]@{
        StreamUrl  = $streamUrl
        MetaUrl    = $metaUrl
        StreamType = $streamType
    }
}

# ── MOSCOW.md ────────────────────────────────────────────────────────────────

function Update-MoscowMd {
    param([object[]]$Stations, [string]$OutPath)

    $rowsBoth   = [System.Collections.Generic.List[PSCustomObject]]::new()
    $rowsStream = [System.Collections.Generic.List[PSCustomObject]]::new()
    $rowsMeta   = [System.Collections.Generic.List[PSCustomObject]]::new()

    foreach ($s in $Stations) {
        $title = Get-Prop $s "title"
        $name  = $(if ($title) { $title } else { Get-Prop $s "name" "Unknown" }).Trim()
        $info = Get-StreamInfo $s
        $row  = [PSCustomObject]@{ Name=$name; Stream=$info.StreamUrl; Meta=$info.MetaUrl }
        if ($info.StreamUrl -and $info.MetaUrl)  { $rowsBoth.Add($row) }
        elseif ($info.StreamUrl)                 { $rowsStream.Add($row) }
        elseif ($info.MetaUrl)                   { $rowsMeta.Add($row) }
    }

    $today   = (Get-Date).ToString("yyyy-MM-dd")
    $nStream = $rowsBoth.Count + $rowsStream.Count
    $nMeta   = $rowsBoth.Count + $rowsMeta.Count

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# Moscow Radio Stations — HLS Streams & Metadata")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> Source: ``$StationsUrl``")
    [void]$sb.AppendLine("> Retrieved: $today | Total stations: $($Stations.Count)")
    [void]$sb.AppendLine("> С потоком: $nStream | С метаданными JSON: $nMeta")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Станция | Stream URL | Metadata URL |")
    [void]$sb.AppendLine("|---------|------------|---------------|")

    foreach ($r in (@($rowsBoth) + @($rowsStream) + @($rowsMeta))) {
        $sc = if ($r.Stream) { "``$($r.Stream)``" } else { "—" }
        $mc = if ($r.Meta)   { "``$($r.Meta)``"   } else { "—" }
        [void]$sb.AppendLine("| $($r.Name) | $sc | $mc |")
    }

    [System.IO.File]::WriteAllText($OutPath, $sb.ToString(), [System.Text.Encoding]::UTF8)
    Write-Host "MOSCOW.md обновлён: $nStream с потоком, $nMeta с метаданными → $OutPath"
}

# ── YAML config creation ─────────────────────────────────────────────────────

function New-SafeFilename {
    param([string]$Name)
    $slug = $Name.ToLower()
    $slug = [System.Text.RegularExpressions.Regex]::Replace($slug, "[^\w\s-]", "")
    $slug = [System.Text.RegularExpressions.Regex]::Replace($slug, "[\s_-]+", "_").Trim("_")
    if (-not $slug) { $slug = "channel" }
    return "$slug.yaml"
}

function New-YamlConfig {
    param(
        [string]$Name,
        [string]$StreamUrl,
        [string]$MetaUrl,
        [string]$StreamType
    )
    $metaLine = if ($MetaUrl) { "  metadata_url: `"$MetaUrl`"" } else { '  metadata_url: ""' }
    @"
# Айва — конфиг канала
# Создан автоматически скриптом Import-RadioPlayerStations.ps1

name: "$Name"

source:
  type: stream
  url: "$StreamUrl"
  stream_type: $StreamType
  allow_http: false
  allow_invalid_ssl: false
  reconnect_delay_seconds: 3
$metaLine

output_format:
  mode: original

auto_start: false
"@
}

function New-Configs {
    param([object[]]$Stations, [string]$Dir)

    if (-not (Test-Path $Dir)) {
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    }

    $created = 0
    $skipped = 0

    foreach ($s in $Stations) {
        $title = Get-Prop $s "title"
        $name  = $(if ($title) { $title } else { Get-Prop $s "name" "Unknown" }).Trim()
        $info = Get-StreamInfo $s
        if (-not $info.StreamUrl) { continue }

        $rawPath  = Get-Prop $s "path"
        $slug     = [System.Text.RegularExpressions.Regex]::Replace($rawPath, "[^\w-]", "")
        $filename = if ($slug) { "$slug.yaml" } else { New-SafeFilename -Name $name }
        $target   = Join-Path $Dir $filename

        if (Test-Path $target) {
            Write-Host "  пропуск (уже есть): $filename"
            $skipped++
            continue
        }

        $yaml = New-YamlConfig `
            -Name       $name `
            -StreamUrl  $info.StreamUrl `
            -MetaUrl    $info.MetaUrl `
            -StreamType $info.StreamType

        [System.IO.File]::WriteAllText($target, $yaml, [System.Text.Encoding]::UTF8)
        Write-Host "  создан: $filename"
        $created++
    }

    [PSCustomObject]@{ Created=$created; Skipped=$skipped }
}

# ── main ─────────────────────────────────────────────────────────────────────

try {
    $stations = Get-Stations
} catch {
    Write-Error "Ошибка: $_"
    exit 1
}

Write-Host "Найдено станций: $($stations.Count)"

$needle = $Filter.Trim()
if ($needle -ne "*") {
    $before   = $stations.Count
    $stations = @($stations | Where-Object {
        $title = Get-Prop $_ "title"
        $name  = if ($title) { $title } else { Get-Prop $_ "name" }
        $name -like "*$needle*"
    })
    Write-Host "После фильтра «$needle»: $($stations.Count) из $before"
}

Update-MoscowMd -Stations $stations -OutPath $MoscowMd

$streamCount = ($stations | Where-Object { (Get-StreamInfo $_).StreamUrl } | Measure-Object).Count

if ($CreateConfigs) {
    Write-Host "`nСоздание конфигов в: $ConfigDir"
    $result = New-Configs -Stations $stations -Dir $ConfigDir
    Write-Host "`nГотово: создано $($result.Created) конфигов (пропущено $($result.Skipped) существующих)"
} else {
    Write-Host "`nДля создания конфигов добавьте флаг -CreateConfigs"
    Write-Host "Будет создано до $streamCount конфигов в $ConfigDir"
}
