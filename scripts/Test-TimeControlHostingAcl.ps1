[CmdletBinding()]
param(
    [string]$ServiceAccount = 'THEBELGIAN\reporting'
)

$ErrorActionPreference = 'Stop'

function Resolve-AccountSid([string]$Identity) {
    return ([Security.Principal.NTAccount]$Identity).Translate(
        [Security.Principal.SecurityIdentifier])
}

function Get-WellKnownSid([Security.Principal.WellKnownSidType]$WellKnown) {
    return [Security.Principal.SecurityIdentifier]::new($WellKnown, $null)
}

function New-SidFileAccessRule(
    [Security.Principal.SecurityIdentifier]$Sid,
    [Security.AccessControl.FileSystemRights]$Rights) {
    return [Security.AccessControl.FileSystemAccessRule]::new(
        $Sid,
        $Rights,
        [Security.AccessControl.AccessControlType]::Allow)
}

function New-SidDirectoryAccessRule(
    [Security.Principal.SecurityIdentifier]$Sid,
    [Security.AccessControl.FileSystemRights]$Rights) {
    return [Security.AccessControl.FileSystemAccessRule]::new(
        $Sid,
        $Rights,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
}

$results = [System.Collections.Generic.List[object]]::new()

# 1) Prove English BUILTIN display name fails on this host (root cause).
try {
    [void]([Security.Principal.NTAccount]'BUILTIN\Administrators').Translate(
        [Security.Principal.SecurityIdentifier])
    $results.Add([pscustomobject]@{ Check = 'BUILTIN\Administrators translate'; Result = 'UNEXPECTED_OK' })
} catch {
    $results.Add([pscustomobject]@{
        Check = 'BUILTIN\Administrators translate'
        Result = 'FAIL_EXPECTED'
        Detail = $_.Exception.Message
    })
}

# 2) Old Protect-Config constructor must fail with same exception.
try {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        'BUILTIN\Administrators',
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow))
    $results.Add([pscustomobject]@{ Check = 'Old AddAccessRule BUILTIN\Administrators'; Result = 'UNEXPECTED_OK' })
} catch {
    $results.Add([pscustomobject]@{
        Check = 'Old AddAccessRule BUILTIN\Administrators'
        Result = 'FAIL_EXPECTED'
        Detail = $_.Exception.Message
    })
}

# 3) SID-based construction must succeed.
$systemSid = Get-WellKnownSid ([Security.Principal.WellKnownSidType]::LocalSystemSid)
$adminsSid = Get-WellKnownSid ([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid)
$serviceSid = Resolve-AccountSid $ServiceAccount

$acl = [Security.AccessControl.FileSecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-SidFileAccessRule $systemSid ([Security.AccessControl.FileSystemRights]::FullControl)))
$acl.AddAccessRule((New-SidFileAccessRule $adminsSid ([Security.AccessControl.FileSystemRights]::FullControl)))
$acl.AddAccessRule((New-SidFileAccessRule $serviceSid ([Security.AccessControl.FileSystemRights]::Read)))
$results.Add([pscustomobject]@{
    Check = 'SID Protect-Config AddAccessRule'
    Result = 'OK'
    Detail = "SYSTEM=$systemSid ADMINS=$adminsSid SERVICE=$serviceSid rules=$($acl.Access.Count)"
})

# 4) Directory rule construction.
$dirRule = New-SidDirectoryAccessRule $serviceSid ([Security.AccessControl.FileSystemRights]'ReadAndExecute')
$results.Add([pscustomobject]@{
    Check = 'SID directory Grant rule'
    Result = 'OK'
    Detail = "$($dirRule.IdentityReference) $($dirRule.FileSystemRights) inherit=$($dirRule.InheritanceFlags)"
})

# 5) Apply Protect-Config style ACL on a temp file (no production paths).
$tempFile = Join-Path $env:TEMP ("tc-acl-test-{0}.json" -f (Get-Date -Format 'HHmmss'))
Set-Content -LiteralPath $tempFile -Value '{"ok":true}' -Encoding UTF8
try {
    Set-Acl -LiteralPath $tempFile -AclObject $acl
    $applied = Get-Acl -LiteralPath $tempFile
    $sidValues = @($applied.Access | ForEach-Object {
        try {
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        } catch {
            [string]$_.IdentityReference
        }
    })
    $hasSystem = $sidValues -contains $systemSid.Value
    $hasAdmins = $sidValues -contains $adminsSid.Value
    $hasService = $sidValues -contains $serviceSid.Value
    if (-not ($hasSystem -and $hasAdmins -and $hasService)) {
        throw "Temp ACL mist expected SIDs. Found: $($sidValues -join ', ')"
    }
    $results.Add([pscustomobject]@{
        Check = 'Temp file Set-Acl SID Protect-Config'
        Result = 'OK'
        Detail = ($sidValues -join '; ')
    })
} finally {
    Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
}

# 6) Target paths exist.
foreach ($path in @(
    'C:\Apps\TheBelgian.TimeControl\releases',
    'C:\Apps\TheBelgian.TimeControl\current',
    'C:\Apps\TheBelgian.TimeControl\config\appsettings.Production.json',
    'C:\Apps\TheBelgian.TimeControl\data',
    'C:\Apps\TheBelgian.TimeControl\logs',
    'C:\Apps\TheBelgian.TimeControl\backups'
)) {
    $results.Add([pscustomobject]@{
        Check = "Path exists: $path"
        Result = $(if (Test-Path -LiteralPath $path) { 'OK' } else { 'MISSING' })
    })
}

$results | Format-Table -AutoSize -Wrap
$failed = @($results | Where-Object {
    $_.Result -notin @('OK', 'FAIL_EXPECTED')
})
if ($failed.Count -gt 0) {
    throw "ACL hosting tests failed: $($failed.Check -join ', ')"
}

Write-Host 'ALL ACL HOSTING TESTS PASSED'
