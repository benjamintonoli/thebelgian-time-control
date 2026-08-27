[CmdletBinding()]
param(
    [string]$ServiceName = 'TheBelgian.TimeControl',
    [string]$DisplayName = 'The Belgian TimeControl',
    [Parameter(Mandatory)] [string]$ReleasePath,
    [string]$RootPath = 'C:\Apps\TheBelgian.TimeControl',
    [string]$DatabasePath = 'C:\Apps\TheBelgian.TimeControl\data\time-control.db',
    [string]$LogDirectory = 'C:\Apps\TheBelgian.TimeControl\logs',
    [string]$ProductionConfigPath = 'C:\Apps\TheBelgian.TimeControl\config\appsettings.Production.json',
    [string]$Url = 'http://127.0.0.1:5260',
    [Parameter(Mandatory)] [string]$ServiceAccount,
    [PSCredential]$Credential,
    [switch]$ServiceAccountIsGmsa,
    [switch]$StartService
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Voer dit installatiescript uit in een verhoogde PowerShell-sessie.'
    }
}

function Assert-ChildPath([string]$Path, [string]$Parent, [string]$Label) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label valt buiten de toegestane root: $fullPath"
    }
    return $fullPath
}

function Remove-DeploymentLink([string]$Path, [string]$Root) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $safePath = Assert-ChildPath $Path $Root 'Deploymentlink'
    $item = Get-Item -LiteralPath $safePath -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Weigering: $safePath is geen junction/reparse point."
    }
    Remove-Item -LiteralPath $safePath -Force
}

function Grant-DirectoryAccess(
    [string]$Path,
    [string]$Identity,
    [Security.AccessControl.FileSystemRights]$Rights) {
    $acl = Get-Acl -LiteralPath $Path
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $Identity,
        $Rights,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Protect-Config([string]$Path, [string]$Identity) {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in @(
        @('NT AUTHORITY\SYSTEM', [Security.AccessControl.FileSystemRights]::FullControl),
        @('BUILTIN\Administrators', [Security.AccessControl.FileSystemRights]::FullControl),
        @($Identity, [Security.AccessControl.FileSystemRights]::Read))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $entry[0], $entry[1], [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Invoke-Sc([string[]]$Arguments) {
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe faalde: $($Arguments -join ' ')"
    }
}

Assert-Administrator

$root = (Resolve-Path -LiteralPath $RootPath).Path
$releasesRoot = (Resolve-Path -LiteralPath (Join-Path $root 'releases')).Path
$release = (Resolve-Path -LiteralPath $ReleasePath).Path
[void](Assert-ChildPath $release $releasesRoot 'Release')
$database = Assert-ChildPath $DatabasePath $root 'Database'
$logs = Assert-ChildPath $LogDirectory $root 'Logdirectory'
$config = (Resolve-Path -LiteralPath $ProductionConfigPath).Path
[void](Assert-ChildPath $config (Join-Path $root 'config') 'Productieconfig')

if (-not (Test-Path -LiteralPath $database -PathType Leaf)) {
    throw "Productiedatabase ontbreekt: $database"
}
$appExe = Join-Path $release 'TheBelgian.TimeControl.Web.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "Gepubliceerde executable ontbreekt: $appExe"
}

$settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
if ($settings.TimeControlCorrectionWrites.Enabled -ne $false -or
    $settings.TimeControlCorrectionWrites.UseMock -ne $false) {
    throw 'Safety gate: TimeControl-correctiewrites en UseMock moeten false zijn.'
}
if ($settings.TimeControlCorrectionWrites.BaseUrl -ne 'http://localhost:5090') {
    throw 'Safety gate: de interne PWS BaseUrl moet http://localhost:5090 zijn.'
}
if ($settings.ConnectionStrings.PlenionOdbc -notmatch 'DSN=PlenionWriteLive') {
    throw 'Safety gate: PlenionOdbc moet de LIVE DSN gebruiken.'
}
if ($settings.ConnectionStrings.TimeControl -notlike "*$database*") {
    throw 'Safety gate: productieconfig verwijst niet naar de gekozen database.'
}
$keysPath = Assert-ChildPath $settings.DataProtection.KeysPath (Join-Path $root 'data') `
    'Data Protection keymap'
if ([string]::IsNullOrWhiteSpace($settings.PowerFleet.ApiKey)) {
    throw 'PowerFleet ApiKey ontbreekt in de beveiligde productieconfig.'
}
if ([string]::IsNullOrWhiteSpace($settings.Geocoding.ApiKey)) {
    throw 'Geocoding ApiKey ontbreekt in de beveiligde productieconfig.'
}

New-Item -ItemType Directory -Force -Path $logs, (Join-Path $root 'backups') | Out-Null
New-Item -ItemType Directory -Force -Path $keysPath | Out-Null
Grant-DirectoryAccess $release $ServiceAccount ([Security.AccessControl.FileSystemRights]'ReadAndExecute')
Grant-DirectoryAccess (Join-Path $root 'data') $ServiceAccount ([Security.AccessControl.FileSystemRights]::Modify)
Grant-DirectoryAccess $logs $ServiceAccount ([Security.AccessControl.FileSystemRights]::Modify)
Protect-Config $config $ServiceAccount

$releaseConfig = Join-Path $release 'appsettings.Production.json'
if (Test-Path -LiteralPath $releaseConfig) {
    $existingHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseConfig).Hash
    $configHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $config).Hash
    if ($existingHash -ne $configHash) {
        throw "Release bevat een afwijkende appsettings.Production.json: $releaseConfig"
    }
    Remove-Item -LiteralPath $releaseConfig -Force
}
New-Item -ItemType HardLink -Path $releaseConfig -Target $config | Out-Null

$current = Join-Path $root 'current'
$next = Join-Path $root 'current.next'
$previous = Join-Path $root 'current.previous'
Remove-DeploymentLink $next $root
New-Item -ItemType Junction -Path $next -Target $release | Out-Null

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $null -ne $existingService -and
    $existingService.Status -eq [ServiceProcess.ServiceControllerStatus]::Running
if ($wasRunning) {
    Stop-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $root "backups\time-control-pre-deploy-$stamp.db"
if ((Test-Path -LiteralPath "$database-wal") -and (Get-Item -LiteralPath "$database-wal").Length -gt 0) {
    throw 'Database-WAL bevat nog data; maak eerst een consistente offline backup.'
}
Copy-Item -LiteralPath $database -Destination $backup

$pointerSwapped = $false
try {
    Remove-DeploymentLink $previous $root
    if (Test-Path -LiteralPath $current) {
        $currentItem = Get-Item -LiteralPath $current -Force
        if (-not ($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Weigering: $current is geen junction."
        }
        Move-Item -LiteralPath $current -Destination $previous
    }
    Move-Item -LiteralPath $next -Destination $current
    $pointerSwapped = $true

    $currentExe = Join-Path $current 'TheBelgian.TimeControl.Web.exe'
    $binaryPath = "`"$currentExe`" --database `"$database`" --urls `"$Url`""
    if ($null -eq $existingService) {
        if ($ServiceAccountIsGmsa) {
            Invoke-Sc @('create', $ServiceName, 'binPath=', $binaryPath, 'start=', 'delayed-auto',
                'obj=', $ServiceAccount, 'password=', '', 'DisplayName=', $DisplayName)
        } else {
            if ($null -eq $Credential) {
                $Credential = Get-Credential -UserName $ServiceAccount `
                    -Message 'Definitief TimeControl-serviceaccount'
            }
            if ($Credential.UserName -ne $ServiceAccount) {
                throw 'Credential.UserName moet exact overeenkomen met ServiceAccount.'
            }
            New-Service -Name $ServiceName -DisplayName $DisplayName `
                -BinaryPathName $binaryPath -StartupType Automatic -Credential $Credential | Out-Null
            Invoke-Sc @('config', $ServiceName, 'start=', 'delayed-auto')
        }
    } else {
        $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        if ($serviceInfo.StartName -ne $ServiceAccount) {
            throw "Bestaande service draait als $($serviceInfo.StartName), niet als $ServiceAccount."
        }
        Invoke-Sc @('config', $ServiceName, 'binPath=', $binaryPath, 'start=', 'delayed-auto')
    }

    Invoke-Sc @('description', $ServiceName,
        'The Belgian TimeControl interne ASP.NET Core productiehost.')
    Invoke-Sc @('failure', $ServiceName, 'reset=', '86400',
        'actions=', 'restart/60000/restart/60000/restart/60000')

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $environment = @(
        'ASPNETCORE_ENVIRONMENT=Production',
        'DOTNET_ENVIRONMENT=Production',
        "ASPNETCORE_URLS=$Url",
        "ConnectionStrings__TimeControl=Data Source=$database",
        "DataProtection__KeysPath=$keysPath",
        'TimeControlCorrectionWrites__Enabled=false',
        'TimeControlCorrectionWrites__UseMock=false',
        'TimeControlCorrectionWrites__BaseUrl=http://localhost:5090',
        "WindowsService__ServiceName=$ServiceName")
    New-ItemProperty -LiteralPath $serviceKey -Name Environment -PropertyType MultiString `
        -Value $environment -Force | Out-Null

    if (-not [Diagnostics.EventLog]::SourceExists($ServiceName)) {
        New-EventLog -LogName Application -Source $ServiceName
    }
    if ($StartService) {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }
} catch {
    if ($pointerSwapped) {
        Remove-DeploymentLink $current $root
        if (Test-Path -LiteralPath $previous) {
            Move-Item -LiteralPath $previous -Destination $current
        }
    }
    if ($wasRunning -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }
    throw
}

[pscustomobject]@{
    ServiceName = $ServiceName
    ServiceAccount = $ServiceAccount
    Release = $release
    Current = $current
    Database = $database
    Backup = $backup
    Url = $Url
    Started = (Get-Service -Name $ServiceName).Status -eq 'Running'
    CorrectionWritesEnabled = $false
}
