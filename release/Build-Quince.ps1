<#
.SYNOPSIS
    Собирает Quince.Service (dotnet publish) в release\<version>, версия берётся из
    Quince.Service\VersionInfo.cs — тот же номер, что показывает приложение и что записан
    в docs\CHANGELOG.md/docs\HISTORY.md.

.DESCRIPTION
    Обёртка над `dotnet publish`, чтобы не помнить точную команду и не разъезжаться в
    именовании папки версии между локальными сборками разных людей. Делает по порядку:
    1. Считывает версию из Quince.Service\VersionInfo.cs (regex по
       `public const string Version = "...";`) — это единственный источник истины,
       откуда версию берёт и само приложение (см. app.LogInformation при старте) и что
       обновляется вручную по стандартной процедуре проекта на каждое изменение.
    2. Опционально (по умолчанию — да, см. -SkipTests) прогоняет `dotnet test`
       Quince.Service.Tests — публиковать заведомо красную сборку смысла нет.
    3. `dotnet publish .\Quince.Service\ -c <Configuration> -o .\release\<version>`.
    4. Обновляет корневой файл VERSION тем же значением, что прочитали на шаге 1 — так
       он не может разойтись с VersionInfo.cs, если кто-то запускает этот скрипт после
       обновления версии в коде.

    Если папка release\<version> уже существует, публикация просто перезапишет её
    содержимое (стандартное поведение dotnet publish) — используйте -Force, чтобы явно
    подтвердить перезапись существующей версии, иначе скрипт остановится с ошибкой (так
    проще заметить, что кто-то забыл обновить VersionInfo.cs после последнего коммита).

.PARAMETER Configuration
    Конфигурация сборки, передаётся в dotnet publish -c. По умолчанию Release.

.PARAMETER SkipTests
    Переключатель. Если указан — пропускает прогон Quince.Service.Tests перед публикацией.

.PARAMETER Force
    Переключатель. Разрешает публикацию в уже существующую папку release\<version>
    (перезапись).

.EXAMPLE
    .\release\Build-Quince.ps1

.EXAMPLE
    # Пересобрать поверх уже существующей версии, не гоняя тесты
    .\release\Build-Quince.ps1 -SkipTests -Force
#>

#Requires -Version 5.1

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    [switch] $SkipTests,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'

# Корень репозитория — родительская папка этого скрипта (release\), а не текущая рабочая
# директория пользователя, чтобы скрипт одинаково работал независимо от того, откуда его
# запустили (`.\release\Build-Quince.ps1` из корня, `.\Build-Quince.ps1` из самой release\, …).
$repoRoot = Split-Path -Parent $PSScriptRoot
$versionInfoPath = Join-Path $repoRoot 'Quince.Service\VersionInfo.cs'
$projectPath = Join-Path $repoRoot 'Quince.Service'
$testsProjectPath = Join-Path $repoRoot 'Quince.Service.Tests'
$versionFilePath = Join-Path $repoRoot 'VERSION'

if (-not (Test-Path -LiteralPath $versionInfoPath)) {
    throw "Не найден $versionInfoPath — запускаете скрипт не из клона quince-dotnet?"
}

$versionInfoContent = Get-Content -LiteralPath $versionInfoPath -Raw
$match = [regex]::Match($versionInfoContent, 'Version\s*=\s*"([^"]+)"')
if (-not $match.Success) {
    throw "Не удалось найти версию в $versionInfoPath (ожидался `Version = `"X.YY.ZZZ`"`)."
}
$version = $match.Groups[1].Value
Write-Host "Версия (из VersionInfo.cs): $version"

if (-not $SkipTests) {
    Write-Host "`nЗапускаю тесты ($testsProjectPath)..."
    & dotnet test $testsProjectPath
    if ($LASTEXITCODE -ne 0) {
        throw "Тесты не прошли (код $LASTEXITCODE) — публикация отменена. Запустите с -SkipTests, чтобы пропустить эту проверку."
    }
}

$outputPath = Join-Path $repoRoot "release\$version"
if ((Test-Path -LiteralPath $outputPath) -and -not $Force) {
    throw "Папка $outputPath уже существует. Передайте -Force, чтобы перезаписать, либо обновите версию в VersionInfo.cs."
}

Write-Host "`nПубликую в $outputPath..."
& dotnet publish $projectPath -c $Configuration -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с ошибкой (код $LASTEXITCODE)."
}

Set-Content -LiteralPath $versionFilePath -Value $version -NoNewline:$false
Write-Host "`nГотово: $outputPath"
Write-Host "Корневой файл VERSION обновлён: $version"
