[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = 'TheBelgian TimeControl Database Backup',
    [string]$BackupScriptPath = 'C:\Dev\thebelgian-time-control\scripts\Backup-TimeControlDatabase.ps1',
    [string]$DatabasePath = 'C:\Apps\TheBelgian.TimeControl\data\time-control.db',
    [string]$BackupDirectory = 'C:\Apps\TheBelgian.TimeControl\backups',
    [string]$LogPath = 'C:\Apps\TheBelgian.TimeControl\logs\database-backup.log',
    [Parameter(Mandatory)] [string]$ServiceAccount,
    [ValidateRange(1, 3650)] [int]$RetentionDays = 35,
    [ValidateRange(1048576, 1073741824)] [long]$MaxLogBytes = 26214400,
    [ValidateRange(1, 100)] [int]$RetainedLogs = 12,
    [PSCredential]$Credential,
    [switch]$ServiceAccountIsGmsa
)

$ErrorActionPreference = 'Stop'
$backupScript = (Resolve-Path -LiteralPath $BackupScriptPath).Path
$database = [IO.Path]::GetFullPath($DatabasePath)
$backupRoot = [IO.Path]::GetFullPath($BackupDirectory)
$log = [IO.Path]::GetFullPath($LogPath)
$logDirectory = [IO.Path]::GetDirectoryName($log)

$shell = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $shell) { $shell = (Get-Command powershell.exe -ErrorAction Stop).Source }

$command = @"
`$ErrorActionPreference = 'Stop'
[IO.Directory]::CreateDirectory('$($logDirectory.Replace("'", "''"))') | Out-Null
[IO.Directory]::CreateDirectory('$($backupRoot.Replace("'", "''"))') | Out-Null
`$logPath = '$($log.Replace("'", "''"))'
if ((Test-Path -LiteralPath `$logPath) -and (Get-Item -LiteralPath `$logPath).Length -ge $MaxLogBytes) {
    `$rotated = `$logPath + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item -LiteralPath `$logPath -Destination `$rotated
    Get-ChildItem -LiteralPath '$($logDirectory.Replace("'", "''"))' -File |
        Where-Object Name -Like '$([IO.Path]::GetFileName($log).Replace("'", "''")).*' |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip $RetainedLogs |
        Remove-Item -Force
}
& '$($backupScript.Replace("'", "''"))' -DatabasePath '$($database.Replace("'", "''"))' `
    -BackupDirectory '$($backupRoot.Replace("'", "''"))' -Reason Daily `
    -RetentionDays $RetentionDays -StopService *>> `$logPath
exit `$LASTEXITCODE
"@
$encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$action = New-ScheduledTaskAction -Execute $shell `
    -Argument "-NoLogo -NoProfile -NonInteractive -EncodedCommand $encodedCommand" `
    -WorkingDirectory $logDirectory
$trigger = New-ScheduledTaskTrigger -Daily -At '02:00'
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 1) -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 10)

if ($ServiceAccountIsGmsa) {
    $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount `
        -LogonType ServiceAccount -RunLevel Highest
    $task = New-ScheduledTask -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal `
        -Description 'Dagelijkse consistente SQLite-backup om 02:00; retentie via backupscript.'
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
    $task = New-ScheduledTask -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal `
        -Description 'Dagelijkse consistente SQLite-backup om 02:00; retentie via backupscript.'
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Credential.Password)
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
