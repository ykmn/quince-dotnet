<#
.SYNOPSIS
    Конвертирует конфиги каналов Айвы в формат Абрикоса (stations/ и playlogs/).

.DESCRIPTION
    Читает папку с YAML-конфигами Айвы, создаёт:
      stations/<GroupId>.yaml   — один YAML-файл с массивом всех каналов (channels:), формат Абрикоса
      playlogs/<id>.yaml        — по одному YAML на каждый источник метаданных (плейлог)

.PARAMETER InputDir
    Папка с YAML-конфигами Айвы (обязательный параметр). Понимает и полный путь
    (например C:\Logger\config), и путь, введённый относительно того места, откуда
    запущен скрипт (например просто config — сработает и из корня репозитория,
    и из папки scripts\, и откуда угодно ещё, см. Resolve-InputDirectory ниже).

.PARAMETER OutputDir
    Папка для сохранения результатов (по умолчанию — InputDir\apricot_export).

.PARAMETER GroupId
    Идентификатор всей группы станций — попадает в верхнеуровневое поле id
    итогового stations/<GroupId>.yaml (по умолчанию — производится из имени папки InputDir).

.PARAMETER GroupName
    Отображаемое имя группы станций — верхнеуровневое поле name (по умолчанию — имя папки InputDir).

.PARAMETER Filter
    Фильтр по названию канала (по умолчанию '*' — все каналы).
    Пример: -Filter 'Европа'

.EXAMPLE
    # Из корня репозитория, полный путь
    .\Quince.Service\scripts\Convert-ToApricot.ps1 -InputDir C:\Logger\config -OutputDir C:\Abricot\config

    # Из корня репозитория, путь относительно текущей папки
    .\Quince.Service\scripts\Convert-ToApricot.ps1 -InputDir config

    # Из самой папки scripts\
    .\Convert-ToApricot.ps1 -InputDir config -Filter 'Ретро' -GroupId msk-hls -GroupName 'Москва HLS'
#>

#Requires -Version 5.1

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $InputDir,

    [Parameter(Position = 1)]
    [string] $OutputDir = "",

    # Deliberately not positional (no Position attribute) — GroupId/GroupName were added after
    # InputDir/OutputDir/Filter already had an established positional convention (see .EXAMPLE),
    # so a bare trailing argument meant for -Filter must not silently land here instead.
    [string] $GroupId = "",

    [string] $GroupName = "",

    [Parameter(Position = 2)]
    [string] $Filter = "*"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# [System.Text.Encoding]::UTF8 writes a BOM; File.WriteAllLines/WriteAllText need this instead.
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function ConvertFrom-YamlSimple {
    <#
    Minimal YAML parser for flat key: value and nested blocks.
    Does not require PSYaml / powershell-yaml module.
    Handles:
        key: value
        key: "quoted value"
        nested:
          subkey: value
    Returns a hashtable (nested hashtables for sub-blocks).
    #>
    param([string[]] $Lines)

    $root  = [ordered]@{}
    $stack = [System.Collections.Generic.Stack[object]]::new()
    $stack.Push([pscustomobject]@{ Dict = $root; Indent = -1 })

    foreach ($raw in $Lines) {
        $line = $raw -replace '#.*$', ''
        if ($line.Trim() -eq '') { continue }

        $indent  = $line.Length - $line.TrimStart().Length
        $trimmed = $line.Trim()

        if ($trimmed.StartsWith('- ')) { continue }
        if ($trimmed -notmatch '^([^:]+):\s*(.*)$') { continue }

        $key      = $Matches[1].Trim()
        # Decide nested-block-vs-scalar from the RAW (still-quoted) text: an explicitly
        # quoted empty string like metadata_url: '' must stay a scalar "", not be mistaken
        # for a "key:" nested-block header (which has truly nothing after the colon) once
        # the quotes are stripped below — both look like '' after stripping otherwise.
        $rawValue = $Matches[2].Trim()
        $value    = $rawValue -replace '^"(.*)"$', '$1' `
                               -replace "^'(.*)'$", '$1'

        while ($stack.Count -gt 1 -and $stack.Peek().Indent -ge $indent) {
            $null = $stack.Pop()
        }
        $parent = $stack.Peek().Dict

        if ($rawValue -eq '') {
            $child = [ordered]@{}
            $parent[$key] = $child
            $stack.Push([pscustomobject]@{ Dict = $child; Indent = $indent })
        } else {
            $parent[$key] = $value
        }
    }
    return $root
}

function Get-SafeId {
    param([string] $Name)
    $map = @{
        'а'='a';'б'='b';'в'='v';'г'='g';'д'='d';'е'='e';'ё'='yo';'ж'='zh'
        'з'='z';'и'='i';'й'='y';'к'='k';'л'='l';'м'='m';'н'='n';'о'='o'
        'п'='p';'р'='r';'с'='s';'т'='t';'у'='u';'ф'='f';'х'='kh';'ц'='ts'
        'ч'='ch';'ш'='sh';'щ'='sch';'ъ'='';'ы'='y';'ь'='';'э'='e';'ю'='yu'
        'я'='ya'
    }
    $sb = [System.Text.StringBuilder]::new()
    foreach ($ch in $Name.ToCharArray()) {
        $lo = $ch.ToString().ToLower()
        if ($map.ContainsKey($lo)) { $null = $sb.Append($map[$lo]) }
        elseif ($lo -match '[a-z0-9]') { $null = $sb.Append($lo) }
        else { $null = $sb.Append('_') }
    }
    return ($sb.ToString() -replace '_+', '_').Trim('_')
}

function Format-YamlString {
    param([string] $Value)
    if ($Value -match '[:#\[\]{}&*!|>''"%@`]' -or $Value -match '^\s' -or $Value -match '\s$') {
        $escaped = $Value -replace '\\', '\\\\' -replace '"', '\"'
        return '"' + $escaped + '"'
    }
    return $Value
}

function Convert-DateFormat { param([string]$Fmt)
    return $Fmt -replace 'YYYY','%Y' -replace 'MM','%m' -replace 'DD','%d' }

function Convert-TimeFormat { param([string]$Fmt)
    return $Fmt -replace '\bhh\b','%H' -replace '\bmm\b','%M' -replace '\bss\b','%S' }

function Get-ConfigValue {
    param([System.Collections.IDictionary]$Dict, [string]$Key, [string]$Default = '')
    if ($Dict.Contains($Key)) { return $Dict[$Key] }
    return $Default
}

<#
Tries -InputDir as: (1) an absolute path, (2) relative to the current directory
(normal shell behaviour — e.g. running from the repo root with -InputDir config),
(3) relative to this script's own folder, (4) relative to this script's parent
folder — covers the common case of config\ being a sibling of scripts\ (as it is
in this repo: Quince.Service\config next to Quince.Service\scripts), so plain
-InputDir config resolves correctly regardless of where the script is invoked from.
#>
function Resolve-InputDirectory {
    param([string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        if (Test-Path $Path -PathType Container) { return (Resolve-Path $Path).Path }
        throw "Папка не найдена: $Path"
    }

    $candidates = @(
        (Join-Path (Get-Location).Path $Path),
        (Join-Path $PSScriptRoot $Path),
        (Join-Path (Split-Path -Parent $PSScriptRoot) $Path)
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate -PathType Container) { return (Resolve-Path $candidate).Path }
    }
    throw "Папка не найдена. Проверенные варианты:`n" + ($candidates -join "`n")
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$InputDir = Resolve-InputDirectory -Path $InputDir
if ($OutputDir -eq '') { $OutputDir = Join-Path $InputDir 'apricot_export' }

# PowerShell's Set-Location/cd updates $PWD (the provider location Join-Path/Test-Path/New-Item
# resolve relative paths against) but NOT [Environment]::CurrentDirectory, which plain .NET calls
# like [System.IO.File]::WriteAllLines use instead — the two can silently point at different
# folders. New-Item below would create stations/playlogs under the *correct* $PWD-relative path,
# while WriteAllLines further down would then try (and fail) to write into the same relative path
# resolved against whatever CurrentDirectory happens to be. Resolving to an absolute path up front
# sidesteps the mismatch entirely.
if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path (Get-Location).Path $OutputDir
}

$InputDirName = Split-Path -Leaf $InputDir
if ($GroupId -eq '')   { $GroupId   = Get-SafeId -Name $InputDirName }
if ($GroupName -eq '') { $GroupName = $InputDirName }

$StationsDir = Join-Path $OutputDir 'stations'
$PlaylogsDir = Join-Path $OutputDir 'playlogs'

New-Item -ItemType Directory -Force -Path $StationsDir | Out-Null
New-Item -ItemType Directory -Force -Path $PlaylogsDir | Out-Null

Write-Host ""
Write-Host "  Айва -> Абрикос  конвертер" -ForegroundColor Cyan
Write-Host "  Входная папка  : $InputDir"
Write-Host "  Выходная папка : $OutputDir"
Write-Host "  Группа станций : $GroupId ($GroupName)"
if ($Filter -ne '*') { Write-Host "  Фильтр         : $Filter" }
Write-Host ""

# Channel configs moved from directly under config/ into config/stations/ (see docs/HISTORY.md) —
# prefer the new layout, fall back to the old flat layout for a not-yet-migrated -InputDir.
$StationsInputDir = Join-Path $InputDir 'stations'
$ChannelSourceDir = if (Test-Path $StationsInputDir -PathType Container) { $StationsInputDir } else { $InputDir }
$files     = Get-ChildItem -Path $ChannelSourceDir -Filter '*.yaml' |
    Where-Object { $_.Name -notin @('app.yaml', 'settings.yaml', 'ldap.yaml', 'users.yaml', 'secret.yaml', 'sessions.yaml') }
$converted = 0
$skipped   = 0

# Collected across all channels, then written as one stations/<GroupId>.yaml with a channels: array.
$stationBlocks = [System.Collections.Generic.List[string[]]]::new()

foreach ($file in $files) {
    $lines  = Get-Content $file.FullName -Encoding UTF8
    $config = ConvertFrom-YamlSimple -Lines $lines

    $name = Get-ConfigValue $config 'name' $file.BaseName

    # Apply filter
    if ($Filter -ne '*' -and $name -notlike "*$Filter*") { $skipped++; continue }

    # Айва channel names carry a trailing "(Россия)" / "(Москва)" city/country qualifier for
    # display purposes; Abricot ids should stay brand-only (business_fm, not business_fm_rossiya).
    $idSource    = ($name -replace '\s*\([^)]*\)\s*$', '').Trim()
    $id          = Get-SafeId -Name $idSource
    $savePath    = Get-ConfigValue $config 'save_path'
    $dateFmt     = Get-ConfigValue $config 'date_folder_format' 'YYYY-MM-DD'
    $timeFmt     = Get-ConfigValue $config 'file_name_format'   'hh-mm-ss'
    $metadataUrl = ''
    $fileExt     = 'mp3'
    $sampleRate  = '48000'

    if ($config.Contains('source') -and $config['source'] -is [System.Collections.IDictionary]) {
        $src         = $config['source']
        $metadataUrl = Get-ConfigValue $src 'metadata_url'
    }

    # input_format.sample_rate is read first as a fallback, but output_format.sample_rate
    # (the rate Айва actually writes to the saved audio file) must win — Abricot reads the
    # file from disk, so it's output_format's rate that matters, not the input stream's.
    if ($config.Contains('input_format') -and $config['input_format'] -is [System.Collections.IDictionary]) {
        $inp = $config['input_format']
        $sampleRate = Get-ConfigValue $inp 'sample_rate' $sampleRate
    }

    if ($config.Contains('output_format') -and $config['output_format'] -is [System.Collections.IDictionary]) {
        $out    = $config['output_format']
        $fileExt    = Get-ConfigValue $out 'file_format' $fileExt
        $sampleRate = Get-ConfigValue $out 'sample_rate' $sampleRate
    }

    # Extract SMB path from save_path last segment
    $smbPath = ''
    if ($savePath -ne '') {
        $parts = ($savePath.TrimEnd('\', '/')) -split '[/\\]'
        if ($parts.Count -ge 1) { $smbPath = $parts[-1] }
    }

    $abricotDateFmt = Convert-DateFormat -Fmt $dateFmt
    $abricotTimeFmt = Convert-TimeFormat -Fmt $timeFmt

    # -----------------------------------------------------------------------
    # One channel block — appended to stations/<GroupId>.yaml's channels: array.
    # -----------------------------------------------------------------------
    $block = [System.Collections.Generic.List[string]]::new()
    $block.Add("  - id: $id")
    $block.Add("    name: $(Format-YamlString $name)")
    $block.Add("    smb:")
    $block.Add("      host: LOGGER-HOST          # TODO: укажите имя или IP хоста")
    $block.Add("      share: LOGGER              # TODO: укажите имя сетевой шары")
    $block.Add("      path: $(Format-YamlString $smbPath)")
    $block.Add("      secret: 1")
    $block.Add("    folder_format: $(Format-YamlString $abricotDateFmt)")
    $block.Add("    file_format: $(Format-YamlString $abricotTimeFmt)")
    $block.Add("    file_extension: $fileExt")
    $block.Add("    sample_rate: $sampleRate")
    if ($metadataUrl -ne '') {
        $block.Add("    playlogs:")
        $block.Add("      - $id")
    } else {
        $block.Add("    playlogs: []")
    }
    $stationBlocks.Add($block.ToArray())

    # -----------------------------------------------------------------------
    # playlogs/<id>.yaml  (only when metadata_url is set)
    # -----------------------------------------------------------------------
    if ($metadataUrl -ne '') {
        $mt = [System.Collections.Generic.List[string]]::new()
        $mt.Add("# Сгенерировано Convert-ToApricot.ps1 из Айвы")
        $mt.Add("")
        $mt.Add("id: $id")
        $mt.Add("name: $(Format-YamlString "$name -- Плейлог")")
        $mt.Add("")
        $mt.Add("sources:")
        $mt.Add("  - priority: 1")
        if ($metadataUrl -eq 'icy') {
            $mt.Add("    # Источник ICY: CSV-файлы метаданных, которые пишет Айва")
            $mt.Add("    local_path: $(Format-YamlString "$savePath\meta")")
        } else {
            $mt.Add("    # JSON-метаданные ($metadataUrl)")
            $mt.Add("    local_path: $(Format-YamlString "$savePath\meta")")
        }
        $mt.Add("    file_mask: ""%Y-%m-%d.csv""")
        $mt.Add("    encoding: ""utf-8""")
        $mt.Add("    delimiter: "",""")
        $mt.Add("    header_skip_prefix: ""EventTime""")
        $mt.Add("")
        $mt.Add("fields:")
        $mt.Add("  datetime:  ""EventTime""")
        $mt.Add("  title:     ""ElemName""")
        $mt.Add("  artist:    ""ElemArtist""")
        $mt.Add("  cls:       ""ElemClass""")
        $mt.Add("  db_id:     ""ElemDbId""")
        $mt.Add("  id_number: ""ElemIdNumber""")
        $mt.Add("")
        $mt.Add("class_colors:")
        $mt.Add('  M: "#558b2f"')
        $mt.Add('  J: "#1565c0"')
        $mt.Add('  C: "#e65100"')
        $mt.Add('  P: "#6a1b9a"')
        $mt.Add('  N: "#37474f"')
        $mt.Add('  default: "#424242"')
        $mt.Add("")
        $mt.Add("class_names:")
        $mt.Add('  M: "Музыка"')
        $mt.Add('  J: "Джингл"')
        $mt.Add('  C: "Реклама"')
        $mt.Add('  P: "Передача"')
        $mt.Add('  N: "Новости"')

        [System.IO.File]::WriteAllLines(
            (Join-Path $PlaylogsDir "$id.yaml"), $mt, $Utf8NoBom)

        Write-Host "  [OK] $($file.Name)  ->  channels[$id] + playlogs/$id.yaml" -ForegroundColor Green
    } else {
        Write-Host "  [OK] $($file.Name)  ->  channels[$id]  (без плейлога)" -ForegroundColor Yellow
    }

    $converted++
}

# ---------------------------------------------------------------------------
# stations/<GroupId>.yaml — one file, all channels as an array.
# ---------------------------------------------------------------------------
$stationsFile = [System.Collections.Generic.List[string]]::new()
$stationsFile.Add("# Сгенерировано Convert-ToApricot.ps1 из Айвы")
$stationsFile.Add("id: $GroupId")
$stationsFile.Add("name: $(Format-YamlString $GroupName)")
$stationsFile.Add("")
$stationsFile.Add("channels:")
foreach ($block in $stationBlocks) {
    foreach ($line in $block) { $stationsFile.Add($line) }
}

$stationsPath = Join-Path $StationsDir "$GroupId.yaml"
[System.IO.File]::WriteAllLines($stationsPath, $stationsFile, $Utf8NoBom)

Write-Host ""
Write-Host "  Готово: $converted конвертировано, $skipped пропущено." -ForegroundColor Cyan
Write-Host "  Станции: $stationsPath"
Write-Host "  Папка результатов: $OutputDir"
Write-Host ""
