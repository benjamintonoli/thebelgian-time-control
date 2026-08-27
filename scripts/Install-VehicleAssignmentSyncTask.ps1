[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = 'TheBelgian TimeControl Vehicle Assignment Sync',
    [Parameter(Mandatory)] [string]$PublishPath,
    [Parameter(Mandatory)] [string]$DatabasePath,
    [Parameter(Mandatory)] [string]$LogPath,
    [Parameter(Mandatory)] [string]$ServiceAccount,
    [PSCredential]$Credential,
    [switch]$ServiceAccountIsGmsa
)

$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishPath).Path
$database = [IO.Path]::GetFullPath($DatabasePath)
$log = [IO.Path]::GetFullPath($LogPath)
$appExe = Join-Path $publish 'TheBelgian.TimeControl.Web.exe'
$appDll = Join-Path $publish 'TheBelgian.TimeControl.Web.dll'

if (Test-Path -LiteralPath $appExe -PathType Leaf) {
    $launch = "& '$($appExe.Replace("'", "''"))'"
} elseif (Test-Path -LiteralPath $appDll -PathType Leaf) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $launch = "& '$($dotnet.Replace("'", "''"))' '$($appDll.Replace("'", "''"))'"
} else {
    throw "Geen gepubliceerde TheBelgian.TimeControl.Web.exe of .dll gevonden in $publish."
}

$shell = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $shell) { $shell = (Get-Command powershell.exe -ErrorAction Stop).Source }
$logDirectory = [IO.Path]::GetDirectoryName($log)
$command = @"
`$ErrorActionPreference = 'Stop'
[IO.Directory]::CreateDirectory('$($logDirectory.Replace("'", "''"))') | Out-Null
$launch --vehicle-assignment-sync --database '$($database.Replace("'", "''"))' --actor 'windows-scheduled-task' *>> '$($log.Replace("'", "''"))'
exit `$LASTEXITCODE
"@
$encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$actionParameters = @{
    Execute = $shell
    Argument = "-NoLogo -NoProfile -NonInteractive -EncodedCommand $encodedCommand"
    WorkingDirectory = $publish
}
$action = New-ScheduledTaskAction @actionParameters
$triggers = @(
    New-ScheduledTaskTrigger -Weekly -WeeksInterval 1 -DaysOfWeek Wednesday -At '03:00'
    New-ScheduledTaskTrigger -Weekly -WeeksInterval 1 -DaysOfWeek Sunday -At '03:00'
)
$settingsParameters = @{
    AllowStartIfOnBatteries = $true
    DontStopIfGoingOnBatteries = $true
    StartWhenAvailable = $true
    MultipleInstances = 'IgnoreNew'
    ExecutionTimeLimit = New-TimeSpan -Hours 2
    RestartCount = 3
    RestartInterval = New-TimeSpan -Minutes 10
}
$settings = New-ScheduledTaskSettingsSet @settingsParameters

if ($ServiceAccountIsGmsa) {
    $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount `
        -LogonType ServiceAccount -RunLevel Highest
    $task = New-ScheduledTask -Action $action -Trigger $triggers `
        -Settings $settings -Principal $principal `
        -Description 'Exact RESCODE/Vehicle.Name vehicle-assignment sync; woensdag en zondag 03:00.'
    if ($PSCmdlet.ShouldProcess($TaskName, 'Scheduled Task installeren of bijwerken')) {
        Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null
    }
} else {
    if ($null -eq $Credential) {
        $Credential = Get-Credential -UserName $ServiceAccount `
            -Message 'Windows-account voor Run whether user is logged on or not'
    }
    if ($Credential.UserName -ne $ServiceAccount) {
        throw 'Credential.UserName moet exact overeenkomen met ServiceAccount.'
    }
    $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount `
        -LogonType Password -RunLevel Highest
    $task = New-ScheduledTask -Action $action -Trigger $triggers `
        -Settings $settings -Principal $principal `
        -Description 'Exact RESCODE/Vehicle.Name vehicle-assignment sync; woensdag en zondag 03:00.'
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode(
        $Credential.Password)
    try {
        $password = [Runtime.InteropServices.Marshal]::PtrToStringUni($pointer)
        if ($PSCmdlet.ShouldProcess($TaskName, 'Scheduled Task installeren of bijwerken')) {
            Register-ScheduledTask -TaskName $TaskName -InputObject $task `
                -User $ServiceAccount -Password $password -Force | Out-Null
        }
    } finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
        }
        Remove-Variable password -ErrorAction SilentlyContinue
    }
}

Get-ScheduledTask -TaskName $TaskName | Select-Object TaskName, State, Principal, Actions, Triggers
