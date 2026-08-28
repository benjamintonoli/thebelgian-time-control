[CmdletBinding()]
param(
    [string]$DatabasePath = 'C:\Apps\TheBelgian.TimeControl\data\time-control.db',
    [string]$BackupDirectory = 'C:\Apps\TheBelgian.TimeControl\backups',
    [ValidateSet('Daily', 'PreDeploy', 'PreFinalization', 'Manual')]
    [string]$Reason = 'Manual',
    [ValidateRange(1, 3650)] [int]$RetentionDays = 35,
    [string]$ServiceName = 'TheBelgian.TimeControl',
    [switch]$StopService
)

$ErrorActionPreference = 'Stop'
$database = (Resolve-Path -LiteralPath $DatabasePath).Path
$backupRoot = [IO.Path]::GetFullPath($BackupDirectory)
if ([IO.Path]::GetPathRoot($backupRoot) -eq $backupRoot) {
    throw 'BackupDirectory mag geen schijfroot zijn.'
}
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$restart = $null -ne $service -and $service.Status -eq 'Running'
if ($restart -and -not $StopService) {
    throw "Service $ServiceName draait. Gebruik -StopService voor een consistente offline backup."
}

try {
    if ($restart) {
        Stop-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    $wal = "$database-wal"
    if ((Test-Path -LiteralPath $wal) -and (Get-Item -LiteralPath $wal).Length -gt 0) {
        throw 'Database-WAL bevat nog data na het stoppen; backup afgebroken.'
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $reasonPart = $Reason.ToLowerInvariant()
    $target = Join-Path $backupRoot "time-control-$reasonPart-$stamp.db"
    $temporary = "$target.partial"
    Copy-Item -LiteralPath $database -Destination $temporary
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $database).Hash
    $backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash
    if ($sourceHash -ne $backupHash) { throw 'Backupverificatie faalde: hashes verschillen.' }
    Move-Item -LiteralPath $temporary -Destination $target

    if ($Reason -eq 'Daily') {
        $cutoff = (Get-Date).AddDays(-$RetentionDays)
        Get-ChildItem -LiteralPath $backupRoot -File -Filter 'time-control-daily-*.db' |
            Where-Object LastWriteTime -LT $cutoff |
            ForEach-Object {
                $candidate = [IO.Path]::GetFullPath($_.FullName)
                if ([IO.Path]::GetDirectoryName($candidate) -ne $backupRoot) {
                    throw "Retentiedoel valt buiten backupmap: $candidate"
                }
                Remove-Item -LiteralPath $candidate -Force
            }
    }

    [pscustomobject]@{
        Database = $database
        Backup = $target
        Reason = $Reason
        Sha256 = $backupHash
        Bytes = (Get-Item -LiteralPath $target).Length
        RetentionDays = $RetentionDays
    }
} finally {
    if ($restart) {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }
}
