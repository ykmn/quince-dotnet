<#
.SYNOPSIS
    Рекурсивный поиск-замена текста во всех файлах папки.

.DESCRIPTION
    Проходит по всем файлам в -Path (включая подпапки), ищет литеральное (не regex)
    вхождение -Find и заменяет на -Replace. Файлы с нулевым байтом (двоичные — exe,
    dll, изображения и т.п.) пропускаются автоматически, чтобы не повредить их
    текстовой перезаписью. Кодировка каждого файла определяется автоматически
    (BOM UTF-8/UTF-16/UTF-32, иначе UTF-8 без BOM) и сохраняется при записи — файл
    не меняет кодировку из-за этого скрипта.
    Поддерживает стандартные параметры PowerShell -WhatIf/-Confirm для предпросмотра
    без реальной записи на диск.

.PARAMETER Find
    Искомая строка (обязательный параметр). Ищется как литеральный текст, не regex.

.PARAMETER Replace
    Строка замены (обязательный параметр; можно пустую строку — тогда Find удаляется).

.PARAMETER Path
    Папка, с которой начинается рекурсивный поиск (обязательный параметр).

.PARAMETER Filter
    Маска имени файла (необязательный параметр, по умолчанию '*' — все файлы). Обычный
    wildcard PowerShell, например '*.csv' или '*.yaml' — ищет и заменяет только в файлах,
    чьё имя подходит под маску, остальные файлы не читаются и не трогаются.

.EXAMPLE
    .\Find-Replace.ps1 -Find 'localhost:5000' -Replace 'localhost:8080' -Path C:\Quince\config

.EXAMPLE
    # Предпросмотр без изменения файлов
    .\Find-Replace.ps1 -Find 'старое' -Replace 'новое' -Path .\config -WhatIf

.EXAMPLE
    # Только в CSV-файлах
    .\Find-Replace.ps1 -Find '—' -Replace '-' -Path \\emg-logger3\d$\LOGGER -Filter *.csv
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $Find,

    [Parameter(Mandatory = $true)]
    [AllowEmptyString()]
    [string] $Replace,

    [Parameter(Mandatory = $true)]
    [string] $Path,

    [Parameter()]
    [string] $Filter = '*'
)

if ($Find -eq '') {
    throw "-Find не может быть пустой строкой."
}

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    throw "Папка не найдена: $Path"
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$filesChanged = 0
$totalReplacements = 0

foreach ($file in (Get-ChildItem -LiteralPath $resolvedPath -Recurse -File -Filter $Filter)) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        if ($bytes -contains 0) {
            Write-Verbose "Пропущен как двоичный: $($file.FullName)"
            continue
        }

        $reader = [System.IO.StreamReader]::new($file.FullName, [System.Text.Encoding]::UTF8, $true)
        $content = $reader.ReadToEnd()
        $encoding = $reader.CurrentEncoding
        $reader.Close()

        $matchCount = ([regex]::Matches($content, [regex]::Escape($Find))).Count
        if ($matchCount -eq 0) { continue }

        if ($PSCmdlet.ShouldProcess($file.FullName, "Заменить '$Find' -> '$Replace' ($matchCount вхожд.)")) {
            $newContent = $content.Replace($Find, $Replace)
            [System.IO.File]::WriteAllText($file.FullName, $newContent, $encoding)
            $filesChanged++
            $totalReplacements += $matchCount
            Write-Host "  $($file.FullName): $matchCount замен(а/ы)"
        }
    }
    catch {
        Write-Warning "Не удалось обработать $($file.FullName): $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Готово: файлов изменено — $filesChanged, всего замен — $totalReplacements."
