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
    бы новые файлы поверх ещё работающей на удалённой машине службы. И проверка
    статуса, и сам стоп/старт идут через CIM/WMI (Get-CimInstance/Invoke-CimMethod
    на классе Win32_Service, удалённо — по DCOM, без WinRM), а не Get-Service
    -ComputerName/sc.exe: у Get-Service параметр -ComputerName вообще отсутствует
    в PowerShell 7/Core (только в Windows PowerShell 5.1) — а этот скрипт может
    запускаться в любой из двух версий, — а текстовый вывод sc.exe query зависит
    от локали Windows на удалённой машине. Для удалённого сценария на целевой
    машине должны быть разрешены удалённое администрирование WMI (брандмауэр
    «Windows Management Instrumentation (WMI)») и валидные права администратора
    текущего пользователя НА НЕЙ — это не проверяется заранее, ошибка будет видна
    как обычное исключение CIM/WMI о недоступности или отказе в доступе.

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

# .ProviderPath, not .Path: for a UNC path, PathInfo.Path comes back provider-qualified
# ("Microsoft.PowerShell.Core\FileSystem::\\server\share\...") rather than the plain path — confirmed
# live (docs/HISTORY.md #133). That breaks Get-UncComputerName's regex (doesn't start with "\\" any
# more) and would confuse robocopy/sc.exe too; .ProviderPath is the plain OS path in both the local
# and UNC case.
$resolvedInstallPath = (Resolve-Path -LiteralPath $InstallPath).ProviderPath
$resolvedSourcePath = (Resolve-Path -LiteralPath $SourcePath).ProviderPath

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

function Get-ServiceStatusInfo {
    # Normalizes local (Get-Service) and remote (Get-CimInstance Win32_Service) queries to the same
    # shape ([pscustomobject] with a .Status string, plus .Cim for the remote case's own instance
    # object) so the rest of the script doesn't need to branch on which one it's looking at. Returns
    # $null if the service isn't found.
    #
    # Deliberately NOT Get-Service -ComputerName for the remote case: that parameter was DROPPED in
    # PowerShell 7/Core (confirmed live — "A parameter cannot be found that matches parameter name
    # 'ComputerName'", docs/HISTORY.md #133), so relying on it would silently misbehave depending on
    # which PowerShell engine happens to run this script (both are supported — see the
    # relaunch-with-whatever-host-invoked-it logic above). CIM's own remoting (DCOM by default on
    # Windows, no WinRM needed) works the same in both editions.
    param([string] $Name, [string] $ComputerName)

    if ($ComputerName) {
        $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ComputerName $ComputerName -ErrorAction SilentlyContinue
        if (-not $svc) { return $null }
        return [pscustomobject]@{ Status = $svc.State; Cim = $svc }
    }
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { return $null }
    return [pscustomobject]@{ Status = $svc.Status.ToString(); Cim = $null }
}

function Invoke-RemoteServiceMethod {
    # Win32_Service's StopService()/StartService() WMI methods — the CIM-remoting counterpart to
    # Stop-Service/Start-Service, used because those cmdlets have no -ComputerName at all (not even in
    # Windows PowerShell 5.1) — sc.exe \\computer stop/start was the other option, but its query output
    # is locale-dependent text (this app's operators are Russian-locale), which is why status querying
    # above went with CIM too, not "sc.exe query".
    param([Microsoft.Management.Infrastructure.CimInstance] $CimService, [string] $MethodName)

    $result = Invoke-CimMethod -InputObject $CimService -MethodName $MethodName
    if ($result.ReturnValue -ne 0) {
        throw "Win32_Service.$MethodName() вернул код ошибки $($result.ReturnValue)."
    }
}

function Wait-ServiceStatus {
    param([string] $Name, [string] $Status, [int] $TimeoutSeconds = 30, [string] $ComputerName)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $current = (Get-ServiceStatusInfo -Name $Name -ComputerName $ComputerName).Status
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

$service = Get-ServiceStatusInfo -Name $ServiceName -ComputerName $remoteComputer

if (-not $service) {
    Write-Warning "Служба '$ServiceName'$serviceLocationSuffix не найдена — копирование будет выполнено без остановки/запуска службы."
}
elseif ($service.Status -ne 'Stopped') {
    if ($PSCmdlet.ShouldProcess("$ServiceName$serviceLocationSuffix", 'Остановить службу')) {
        Write-Host "Останавливаю службу $ServiceName$serviceLocationSuffix..."
        if ($remoteComputer) {
            Invoke-RemoteServiceMethod -CimService $service.Cim -MethodName 'StopService'
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
    switch ($LASTEXITCODE) {
        0 { Write-Host "Файлы не изменились (код robocopy: $LASTEXITCODE)." }
        1 { Write-Host "Все файлы успешно скопированы (код robocopy: $LASTEXITCODE)." }
        2 { Write-Host "В целевом кателоге есть несколько файлов, которые отсутствуют в исходном каталоге, файлы не скопированы (код robocopy: $LASTEXITCODE)." }
        3 { Write-Host "Некоторые файлы скопированы, были представлены дополнительные файлы (код robocopy: $LASTEXITCODE)."}
        5 { Write-Host "Некоторые файлы скопированы, некоторые файлы были несогласованы. Сбоев нет (код robocopy: $LASTEXITCODE)." }
        6 { Write-Host "Существуют дополнительные и несогласованные файлы. Файлы уже существуют в целевом каталоге (код robocopy: $LASTEXITCODE)." }
        7 { Write-Host "Файлы скопированы (код robocopy: $LASTEXITCODE)." }
        default { if ($LASTEXITCODE -ge 8) {
                throw "robocopy завершился с ошибкой (код $LASTEXITCODE)."   
            }
        }
    }
    Write-Host "Копирование завершено (код robocopy: $LASTEXITCODE)."
}

if ($service) {
    if ($PSCmdlet.ShouldProcess("$ServiceName$serviceLocationSuffix", 'Запустить службу')) {
        Write-Host "Запускаю службу $ServiceName$serviceLocationSuffix..."
        if ($remoteComputer) {
            Invoke-RemoteServiceMethod -CimService $service.Cim -MethodName 'StartService'
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
