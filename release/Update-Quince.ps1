<#
.SYNOPSIS
    Обновление установленного Quince.Service новой версией с остановкой/запуском службы.

.DESCRIPTION
    Автоматизирует ручную процедуру обновления, описанную в HOWTO.md:
    1. Проверяет наличие службы QuinceAudioLogger. Если она есть — останавливает её
       и ждёт перехода в состояние Stopped.
    2. Копирует (robocopy /E, с перезаписью существующих файлов) содержимое папки
       новой версии (-SourcePath) в папку установленного приложения (-InstallPath).
       Папка config\ по умолчанию НЕ копируется, чтобы не затереть локальные
       настройки/секреты (settings.yaml и т.п.), и папку log\ трогать вообще не
       нужно — её не бывает в публикации. Передайте -CopyConfig, чтобы всё же
       перезаписать config\ из новой версии.
    3. Если служба была найдена — запускает её снова и ждёт состояния Running.

    Скрипт требует прав администратора (управление службами) и при необходимости
    перезапускает себя с UAC-запросом, как остальные *.bat-скрипты в tools\.
    Поддерживает -WhatIf/-Confirm для предпросмотра без реальных изменений.

    -InstallPath/-SourcePath могут быть абсолютными, относительными (от текущей
    рабочей директории) или сетевыми (\\server\share\...) путями. Если
    -InstallPath сетевой (UNC), служба проверяется/останавливается/запускается
    НА ТОМ УДАЛЁННОМ КОМПЬЮТЕРЕ (имя берётся из самого пути, \\<компьютер>\...),
    а не на локальном, где выполняется сам скрипт — иначе обновление скопировало
    бы новые файлы поверх ещё работающей на удалённой машине службы. Проверка
    статуса идёт через Get-Service -ComputerName (тот же удалённый вызов, что
    использует sc.exe), а сам стоп/старт — через sc.exe \\<компьютер> stop/start,
    поскольку у Stop-Service/Start-Service нет параметра -ComputerName. Для этого
    на удалённой машине должны быть разрешены удалённое управление службами
    (Remote Registry/RPC, брандмауэр «Удалённое управление службами») и валидные
    права администратора текущего пользователя НА НЕЙ — это не проверяется
    заранее, ошибка будет вида "Отказано в доступе" от sc.exe/Get-Service.

.PARAMETER InstallPath
    Папка установленного приложения, которую нужно обновить (обязательный).

.PARAMETER SourcePath
    Папка с новой версией приложения (результат dotnet publish), откуда копировать
    файлы (обязательный).

.PARAMETER CopyConfig
    Переключатель. Если указан — config\ тоже копируется из новой версии поверх
    существующего. По умолчанию пропускается.

.EXAMPLE
    .\Update-Quince.ps1 -InstallPath C:\Quince -SourcePath .\1.00.001

.EXAMPLE
    # Предпросмотр без реальных изменений
    .\Update-Quince.ps1 -InstallPath C:\Quince -SourcePath \\fileserver\quince\1.00.001 -WhatIf

.EXAMPLE
    # Обновить вместе с config\
    .\Update-Quince.ps1 -InstallPath C:\Quince -SourcePath .\1.00.001 -CopyConfig
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallPath,

    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [switch] $CopyConfig
)

$ServiceName = 'QuinceAudioLogger'

# Пути резолвятся в абсолютные ДО повышения прав: относительный путь считается от
# текущей рабочей директории пользователя, а не от той, в которой окажется
# перезапущенный elevated-процесс (Start-Process -Verb RunAs на реальном рабочем
# столе обычно сбрасывает рабочую директорию, например на System32) — иначе
# относительные -InstallPath/-SourcePath ломаются молча именно на этом шаге.
if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) {
    throw "Папка установленного приложения не найдена: $InstallPath"
}
if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
    throw "Папка новой версии не найдена: $SourcePath"
}

$resolvedInstallPath = (Resolve-Path -LiteralPath $InstallPath).Path
$resolvedSourcePath = (Resolve-Path -LiteralPath $SourcePath).Path

# Повышение прав, если требуется (управление службами и запись в папку установки).
$currentPrincipal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Requesting administrative privileges...'
    # Уже resolve-нутые абсолютные пути идут в relaunch вместо сырых значений
    # $PSBoundParameters — так относительные пути не зависят от рабочей директории
    # elevated-процесса.
    $relaunchParams = @{}
    foreach ($key in $PSBoundParameters.Keys) { $relaunchParams[$key] = $PSBoundParameters[$key] }
    $relaunchParams['InstallPath'] = $resolvedInstallPath
    $relaunchParams['SourcePath'] = $resolvedSourcePath

    # Перезапускаем тем же движком (powershell.exe или pwsh.exe), которым запущен
    # текущий процесс, а не жёстко заданным — pwsh.exe может быть не установлен.
    $hostExePath = (Get-Process -Id $PID).Path

    # ProcessStartInfo.ArgumentList (а не Start-Process -ArgumentList, который лишь
    # склеивает элементы в одну строку через пробел с ручными кавычками) — экранирование
    # каждого аргумента делает сам .NET, включая путь с завершающим "\" перед кавычкой:
    # ручное «"$value"» в таком случае ломает границу токена ("\"" читается как
    # экранированная кавычка, а не конец аргумента), и следующий именованный параметр
    # ошибочно считается непереданным.
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $hostExePath
    $psi.UseShellExecute = $true
    $psi.Verb = 'runas'
    $psi.WorkingDirectory = $resolvedInstallPath
    foreach ($arg in @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)) {
        $psi.ArgumentList.Add($arg)
    }
    foreach ($key in $relaunchParams.Keys) {
        $value = $relaunchParams[$key]
        if ($value -is [System.Management.Automation.SwitchParameter] -or $value -is [bool]) {
            if ($value) { $psi.ArgumentList.Add("-$key") }
        }
        else {
            $psi.ArgumentList.Add("-$key")
            $psi.ArgumentList.Add([string] $value)
        }
    }
    [System.Diagnostics.Process]::Start($psi) | Out-Null
    exit
}

function Get-UncComputerName {
    # Extracts "<computer>" from a "\\<computer>\share\..." path — null for a local
    # (drive-letter or relative) path, meaning "manage the service on this machine".
    param([string] $Path)
    if ($Path -match '^\\\\([^\\]+)\\') { return $Matches[1] }
    return $null
}

function Wait-ServiceStatus {
    param([string] $Name, [string] $Status, [int] $TimeoutSeconds = 30, [string] $ComputerName)

    $getParams = @{ Name = $Name }
    if ($ComputerName) { $getParams['ComputerName'] = $ComputerName }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $current = (Get-Service @getParams).Status
    } while ($current -ne $Status -and (Get-Date) -lt $deadline)

    if ($current -ne $Status) {
        throw "Служба $Name не перешла в состояние '$Status' за $TimeoutSeconds сек. (сейчас: $current)"
    }
}

# InstallPath (not SourcePath — that's just where we copy FROM) decides which computer actually
# runs the service: a UNC -InstallPath means the app is installed on that remote machine, so the
# service lives there too, regardless of which machine this script itself runs on.
$remoteComputer = Get-UncComputerName -Path $resolvedInstallPath
$serviceLocationSuffix = if ($remoteComputer) { " на \\$remoteComputer" } else { "" }

$serviceQueryParams = @{ Name = $ServiceName; ErrorAction = 'SilentlyContinue' }
if ($remoteComputer) { $serviceQueryParams['ComputerName'] = $remoteComputer }
$service = Get-Service @serviceQueryParams

if (-not $service) {
    Write-Warning "Служба '$ServiceName'$serviceLocationSuffix не найдена — копирование будет выполнено без остановки/запуска службы."
}
elseif ($service.Status -ne 'Stopped') {
    if ($PSCmdlet.ShouldProcess("$ServiceName$serviceLocationSuffix", 'Остановить службу')) {
        Write-Host "Останавливаю службу $ServiceName$serviceLocationSuffix..."
        if ($remoteComputer) {
            # Stop-Service has no -ComputerName — sc.exe is the one tool here that can target a
            # remote machine directly, same remote-management channel Get-Service -ComputerName
            # already uses under the hood.
            & sc.exe "\\$remoteComputer" stop $ServiceName | Write-Verbose
            if ($LASTEXITCODE -ne 0) { throw "sc.exe stop$serviceLocationSuffix завершился с кодом $LASTEXITCODE." }
        }
        else {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
        }
        Wait-ServiceStatus -Name $ServiceName -Status 'Stopped' -ComputerName $remoteComputer
        Write-Host "Служба остановлена."
    }
}
else {
    Write-Host "Служба $ServiceName$serviceLocationSuffix уже остановлена."
}

$excludeDirs = @('log')
if (-not $CopyConfig) {
    $excludeDirs += 'config'
}

$robocopyArgs = @($resolvedSourcePath, $resolvedInstallPath, '/E', '/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS')
if ($excludeDirs.Count -gt 0) { $robocopyArgs += '/XD'; $robocopyArgs += $excludeDirs }

if ($PSCmdlet.ShouldProcess("$resolvedSourcePath -> $resolvedInstallPath", 'Скопировать файлы новой версии')) {
    Write-Host "Копирую файлы из $resolvedSourcePath в $resolvedInstallPath..."
    & robocopy.exe @robocopyArgs | Write-Verbose
    # robocopy: коды 0-7 — успех/информационные, 8+ — ошибка.
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy завершился с ошибкой (код $LASTEXITCODE)."
    }
    Write-Host "Копирование завершено (код robocopy: $LASTEXITCODE)."
}

if ($service) {
    if ($PSCmdlet.ShouldProcess("$ServiceName$serviceLocationSuffix", 'Запустить службу')) {
        Write-Host "Запускаю службу $ServiceName$serviceLocationSuffix..."
        if ($remoteComputer) {
            & sc.exe "\\$remoteComputer" start $ServiceName | Write-Verbose
            if ($LASTEXITCODE -ne 0) { throw "sc.exe start$serviceLocationSuffix завершился с кодом $LASTEXITCODE." }
        }
        else {
            Start-Service -Name $ServiceName -ErrorAction Stop
        }
        Wait-ServiceStatus -Name $ServiceName -Status 'Running' -ComputerName $remoteComputer
        Write-Host "Служба запущена."
    }
}

Write-Host ""
Write-Host "Готово."
Read-Host "Нажмите Enter для выхода"
