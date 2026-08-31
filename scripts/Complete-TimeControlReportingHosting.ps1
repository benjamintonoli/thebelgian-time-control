[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ReleasePath = 'C:\Apps\TheBelgian.TimeControl\releases\a1f4d616f2a9454c16473b6a18b7d503ec9d3192',
    [string]$ServiceAccount = 'THEBELGIAN\reporting',
    [string]$RootPath = 'C:\Apps\TheBelgian.TimeControl',
    [string]$DatabasePath = 'C:\Apps\TheBelgian.TimeControl\data\time-control.db',
    [string]$ScriptsRoot = 'C:\Dev\thebelgian-time-control\scripts',
    [switch]$SkipVehicleSyncManualRun,
    [switch]$SkipBackupManualRun
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Voer dit script uit in een verhoogde interactieve PowerShell-sessie.'
    }
}

function Write-Status([string]$Message) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message"
}

function Test-TimeControlHealth {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5260/health' -TimeoutSec 5
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Start-ManualTimeControlHost {
    param(
        [string]$RootPath,
        [string]$DatabasePath
    )
    $current = Join-Path $RootPath 'current'
    $exe = Join-Path $current 'TheBelgian.TimeControl.Web.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Handmatige host executable ontbreekt: $exe"
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.WorkingDirectory = $current
    $psi.Arguments = "--database `"$DatabasePath`" --urls `"http://127.0.0.1:5260`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $psi.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
    $psi.EnvironmentVariables['ASPNETCORE_URLS'] = 'http://127.0.0.1:5260'
    $proc = [Diagnostics.Process]::Start($psi)
    if ($null -eq $proc) {
        throw 'Kon handmatige TimeControl-host niet starten.'
    }

    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Seconds 2
        if (Test-TimeControlHealth) {
            Write-Status "ROLLBACK TO MANUAL HOST SUCCESSFUL (PID=$($proc.Id))"
            return $proc.Id
        }
    } while ((Get-Date) -lt $deadline)

    throw "Handmatige TimeControl-host startte (PID=$($proc.Id)) maar /health werd niet 200."
}

function Restore-ManualHostIfNeeded {
    param(
        [bool]$ManualHostWasStopped,
        [string]$RootPath,
        [string]$DatabasePath
    )
    if (-not $ManualHostWasStopped) { return }
    if (Test-TimeControlHealth) {
        Write-Status 'Origin 127.0.0.1:5260 is bereikbaar na failure; geen handmatige rollback nodig.'
        return
    }
    Write-Status 'Origin down na failure — herstart handmatige productiehost...'
    [void](Start-ManualTimeControlHost -RootPath $RootPath -DatabasePath $DatabasePath)
}

Assert-Administrator

Write-Status "Credential ophalen voor $ServiceAccount (wordt niet gelogd/opgeslagen in bestanden)."
$credential = Get-Credential -UserName $ServiceAccount `
    -Message 'Wachtwoord voor TimeControl Windows Service + Scheduled Tasks. Wordt alleen aan Windows SCM/Task Scheduler doorgegeven.'
if ($null -eq $credential) {
    throw 'REPORTING CREDENTIAL REQUIRED'
}
if ($credential.UserName -ne $ServiceAccount) {
    throw "Credential.UserName moet exact '$ServiceAccount' zijn. Ontvangen: $($credential.UserName)"
}
if ([string]::IsNullOrWhiteSpace($credential.GetNetworkCredential().Password)) {
    throw 'REPORTING CREDENTIAL REQUIRED'
}

$current = Join-Path $RootPath 'current'
$serviceName = 'TheBelgian.TimeControl'
$manualHostWasStopped = $false

try {
    # Stop leftover manual listener if present (must not collide with service).
    $listeners = @(Get-NetTCPConnection -LocalPort 5260 -State Listen -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $proc = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        if ($null -ne $proc) {
            Write-Status "Stop handmatige listener PID $($proc.Id) ($($proc.ProcessName))."
            Stop-Process -Id $proc.Id -Force
            Start-Sleep -Seconds 2
            $manualHostWasStopped = $true
        }
    }

    Write-Status 'Windows Service installeren/configureren...'
    & (Join-Path $ScriptsRoot 'Install-TimeControlService.ps1') `
        -ReleasePath $ReleasePath `
        -ServiceAccount $ServiceAccount `
        -Credential $credential `
        -StartService

    $service = Get-Service -Name $serviceName
    if ($service.Status -ne 'Running') {
        throw "Service $serviceName startte niet (Status=$($service.Status))."
    }

    $svcProc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $exePath = $svcProc.PathName
    Write-Status "Service Running. StartName=$($svcProc.StartName) Path=$exePath"

    $health = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5260/health' -TimeoutSec 15
    if ($health.StatusCode -ne 200) { throw "Health faalde: $($health.StatusCode)" }
    Write-Status 'Local /health = 200'

    try {
        $admin = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5260/Admin/TimeControl' -TimeoutSec 15
        throw "Admin zonder JWT gaf $($admin.StatusCode), verwacht 401."
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($code -ne 401) { throw "Admin zonder JWT gaf $code, verwacht 401. $($_.Exception.Message)" }
    }
    Write-Status 'Local /Admin/TimeControl zonder JWT = 401'

    Write-Status 'Vehicle Assignment Sync task installeren...'
    & (Join-Path $ScriptsRoot 'Install-VehicleAssignmentSyncTask.ps1') `
        -PublishPath $current `
        -DatabasePath $DatabasePath `
        -LogPath (Join-Path $RootPath 'logs\vehicle-sync.log') `
        -ServiceAccount $ServiceAccount `
        -Credential $credential `
        -Actor 'SYSTEM_VEHICLE_SYNC'

    Write-Status 'Monthly Review Prepare task installeren...'
    & (Join-Path $ScriptsRoot 'Install-MonthlyReviewPrepareTask.ps1') `
        -PublishPath $current `
        -DatabasePath $DatabasePath `
        -LogPath (Join-Path $RootPath 'logs\monthly-prepare.log') `
        -ServiceAccount $ServiceAccount `
        -Credential $credential `
        -Actor 'SYSTEM_MONTHLY_PREPARE'

    Write-Status 'Database Backup task installeren...'
    & (Join-Path $ScriptsRoot 'Install-TimeControlDatabaseBackupTask.ps1') `
        -ServiceAccount $ServiceAccount `
        -Credential $credential

    if (-not $SkipVehicleSyncManualRun) {
        Write-Status 'Gecontroleerde manual run: Vehicle Assignment Sync...'
        $vehicleTask = 'TheBelgian TimeControl Vehicle Assignment Sync'
        Start-ScheduledTask -TaskName $vehicleTask
        $deadline = (Get-Date).AddMinutes(30)
        do {
            Start-Sleep -Seconds 5
            $info = Get-ScheduledTaskInfo -TaskName $vehicleTask
            $state = (Get-ScheduledTask -TaskName $vehicleTask).State
            Write-Status "Vehicle sync state=$state lastResult=$($info.LastTaskResult)"
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        if ((Get-ScheduledTask -TaskName $vehicleTask).State -eq 'Running') {
            throw 'Vehicle sync draait nog na 30 minuten.'
        }
        if ((Get-ScheduledTaskInfo -TaskName $vehicleTask).LastTaskResult -ne 0) {
            throw "Vehicle sync faalde: LastTaskResult=$((Get-ScheduledTaskInfo -TaskName $vehicleTask).LastTaskResult)"
        }
    }

    if (-not $SkipBackupManualRun) {
        Write-Status 'Gecontroleerde manual run: Database Backup...'
        $backupTask = 'TheBelgian TimeControl Database Backup'
        Start-ScheduledTask -TaskName $backupTask
        $deadline = (Get-Date).AddMinutes(10)
        do {
            Start-Sleep -Seconds 3
            $info = Get-ScheduledTaskInfo -TaskName $backupTask
            $state = (Get-ScheduledTask -TaskName $backupTask).State
            Write-Status "Backup state=$state lastResult=$($info.LastTaskResult)"
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        if ((Get-ScheduledTask -TaskName $backupTask).State -eq 'Running') {
            throw 'Backup task draait nog na 10 minuten.'
        }
        if ((Get-ScheduledTaskInfo -TaskName $backupTask).LastTaskResult -ne 0) {
            throw "Backup task faalde: LastTaskResult=$((Get-ScheduledTaskInfo -TaskName $backupTask).LastTaskResult)"
        }
    }

    Write-Status 'Service restart test...'
    $beforePid = (Get-NetTCPConnection -LocalPort 5260 -State Listen -ErrorAction Stop).OwningProcess |
        Select-Object -First 1
    Restart-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    Start-Sleep -Seconds 2
    $afterPid = (Get-NetTCPConnection -LocalPort 5260 -State Listen -ErrorAction Stop).OwningProcess |
        Select-Object -First 1
    $health2 = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5260/health' -TimeoutSec 15
    if ($health2.StatusCode -ne 200) { throw 'Health na restart faalde.' }
    Write-Status "Restart OK. BeforePID=$beforePid AfterPID=$afterPid Health=200"

    [pscustomobject]@{
        Service = (Get-Service -Name $serviceName).Status
        StartName = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartName
        ListenerPid = $afterPid
        Health = 200
        VehicleTask = (Get-ScheduledTask -TaskName 'TheBelgian TimeControl Vehicle Assignment Sync').State
        MonthlyTask = (Get-ScheduledTask -TaskName 'TimeControl Monthly Review Prepare').State
        BackupTask = (Get-ScheduledTask -TaskName 'TheBelgian TimeControl Database Backup').State
        Message = 'TimeControl permanent hosting steps completed. Review final verification separately.'
    }
} catch {
    Write-Status "FAILURE: $($_.Exception.Message)"
    try {
        Restore-ManualHostIfNeeded -ManualHostWasStopped $manualHostWasStopped `
            -RootPath $RootPath -DatabasePath $DatabasePath
    } catch {
        Write-Status "ROLLBACK FAILED: $($_.Exception.Message)"
    }
    throw
} finally {
    Remove-Variable credential -ErrorAction SilentlyContinue
    [GC]::Collect()
}
