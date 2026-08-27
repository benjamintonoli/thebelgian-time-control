[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = 'TimeControl Monthly Review Prepare',
    [Parameter(Mandatory)] [string]$PublishPath,
    [Parameter(Mandatory)] [string]$DatabasePath,
    [Parameter(Mandatory)] [string]$LogPath,
    [Parameter(Mandatory)] [string]$ServiceAccount,
    [string]$Actor = 'SYSTEM_MONTHLY_PREPARE',
    [ValidateRange(1048576, 1073741824)] [long]$MaxLogBytes = 52428800,
    [ValidateRange(1, 100)] [int]$RetainedLogs = 12,
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
$productionConfig = Join-Path $publish 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $productionConfig -PathType Leaf)) {
    throw "Productieconfig ontbreekt: $productionConfig"
}
$safeActor = $Actor.Trim()
if ([string]::IsNullOrWhiteSpace($safeActor)) { throw 'Actor is verplicht.' }
$command = @"
`$ErrorActionPreference = 'Stop'
[IO.Directory]::CreateDirectory('$($logDirectory.Replace("'", "''"))') | Out-Null
Set-Location -LiteralPath '$($publish.Replace("'", "''"))'
`$env:ASPNETCORE_ENVIRONMENT = 'Production'
`$logPath = '$($log.Replace("'", "''"))'
if ((Test-Path -LiteralPath `$logPath) -and (Get-Item -LiteralPath `$logPath).Length -ge $MaxLogBytes) {
    `$rotated = `$logPath + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item -LiteralPath `$logPath -Destination `$rotated
    Get-ChildItem -LiteralPath '$($logDirectory.Replace("'", "''"))' -File |
        Where-Object Name -Like '$([IO.Path]::GetFileName($log).Replace("'", "''")).*' |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip $RetainedLogs |
        Remove-Item -Force
}
$launch --prepare-monthly-review --database '$($database.Replace("'", "''"))' --actor '$($safeActor.Replace("'", "''"))' *>> `$logPath
exit `$LASTEXITCODE
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$action = New-ScheduledTaskAction -Execute $shell `
    -Argument "-NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded" `
    -WorkingDirectory $publish
$start = Get-Date -Year (Get-Date).Year -Month (Get-Date).Month -Day 15 -Hour 4 -Minute 0 -Second 0
if ($start -lt (Get-Date)) { $start = $start.AddMonths(1) }

# New-ScheduledTaskTrigger exposeert geen maandtrigger. Bouw uitsluitend het triggerdeel
# via de officiële Task Scheduler XML-vorm en laat Register-ScheduledTask de taak valideren.
$actionXml = [Security.SecurityElement]::Escape($action.Execute)
$argumentsXml = [Security.SecurityElement]::Escape($action.Arguments)
$workingXml = [Security.SecurityElement]::Escape($publish)
$accountXml = [Security.SecurityElement]::Escape($ServiceAccount)
$logonType = if ($ServiceAccountIsGmsa) { 'ServiceAccount' } else { 'Password' }
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Maandelijkse read-only urencontrolevoorbereiding op de 15e om 04:00.</Description></RegistrationInfo>
  <Triggers><CalendarTrigger><StartBoundary>$($start.ToString('yyyy-MM-ddT04:00:00'))</StartBoundary><Enabled>true</Enabled><ScheduleByMonth><DaysOfMonth><Day>15</Day></DaysOfMonth><Months><January/><February/><March/><April/><May/><June/><July/><August/><September/><October/><November/><December/></Months></ScheduleByMonth></CalendarTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$accountXml</UserId><LogonType>$logonType</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT8H</ExecutionTimeLimit><Enabled>true</Enabled></Settings>
  <Actions Context="Author"><Exec><Command>$actionXml</Command><Arguments>$argumentsXml</Arguments><WorkingDirectory>$workingXml</WorkingDirectory></Exec></Actions>
</Task>
"@

if ($ServiceAccountIsGmsa) {
    if ($PSCmdlet.ShouldProcess($TaskName, 'Scheduled Task installeren of bijwerken')) {
        Register-ScheduledTask -TaskName $TaskName -Xml $xml -User $ServiceAccount -Force | Out-Null
    }
} else {
    if ($null -eq $Credential) {
        $Credential = Get-Credential -UserName $ServiceAccount `
            -Message 'Windows-account voor Run whether user is logged on or not'
    }
    if ($Credential.UserName -ne $ServiceAccount) {
        throw 'Credential.UserName moet exact overeenkomen met ServiceAccount.'
    }
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Credential.Password)
    try {
        $password = [Runtime.InteropServices.Marshal]::PtrToStringUni($pointer)
        if ($PSCmdlet.ShouldProcess($TaskName, 'Scheduled Task installeren of bijwerken')) {
            Register-ScheduledTask -TaskName $TaskName -Xml $xml `
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
