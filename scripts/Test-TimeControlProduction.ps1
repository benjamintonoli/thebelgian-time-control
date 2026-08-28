[CmdletBinding()]
param(
    [string]$ServiceName = 'TheBelgian.TimeControl',
    [string]$OriginUrl = 'http://127.0.0.1:5260',
    [string]$DatabasePath = 'C:\Apps\TheBelgian.TimeControl\data\time-control.db',
    [string]$CurrentPath = 'C:\Apps\TheBelgian.TimeControl\current',
    [string]$PwsUrl = 'http://localhost:5090',
    [string]$Month = '2026-07'
)

$ErrorActionPreference = 'Stop'
function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -ne $Expected) { throw "$Message Verwacht=$Expected Werkelijk=$Actual" }
}

$service = Get-Service -Name $ServiceName
Assert-Equal $service.Status 'Running' 'TimeControl-service draait niet.'
$listener = Get-NetTCPConnection -LocalPort 5260 -State Listen -ErrorAction Stop
if (@($listener | Where-Object LocalAddress -NE '127.0.0.1').Count -gt 0 -or
    $listener.LocalAddress -notcontains '127.0.0.1') {
    throw 'Poort 5260 luistert niet uitsluitend op de verwachte loopbackbinding.'
}
$database = (Resolve-Path -LiteralPath $DatabasePath).Path
$current = (Resolve-Path -LiteralPath $CurrentPath).Path
$configPath = Join-Path $current 'appsettings.Production.json'
$settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
Assert-Equal $settings.TimeControlCorrectionWrites.Enabled $true 'TimeControl-writeflag moet enabled zijn.'
Assert-Equal $settings.TimeControlCorrectionWrites.UseMock $false 'UseMock moet false zijn.'
Assert-Equal $settings.TimeControlCorrectionWrites.BaseUrl $PwsUrl 'Interne PWS URL wijkt af.'
Assert-Equal $settings.CloudflareAccess.Enabled $true 'CloudflareAccess moet enabled zijn.'
if ($settings.ConnectionStrings.TimeControl -notlike "*$database*") {
    throw 'Productieconfig verwijst niet naar de verwachte database.'
}

$health = Invoke-RestMethod -Uri "$OriginUrl/health" -TimeoutSec 10
Assert-Equal $health.status 'ok' 'TimeControl-liveness faalde.'
$cockpit = Invoke-WebRequest -UseBasicParsing -Uri "$OriginUrl/Admin/TimeControl?month=$Month" -TimeoutSec 15
Assert-Equal $cockpit.StatusCode 200 'Cockpit geeft geen HTTP 200.'
if (-not $cockpit.Content.Contains('Laatste voertuigsynchronisatie',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'LastVehicleSync is niet zichtbaar.'
}
if (-not $cockpit.Content.Contains('Juli 2026', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Juli 2026 is niet bereikbaar.'
}
$pwsHealth = Invoke-RestMethod -Uri "$PwsUrl/health" -TimeoutSec 10
Assert-Equal $pwsHealth.status 'ok' 'PWS-health faalde.'
$pwsFeatures = (Invoke-RestMethod -Uri "$PwsUrl/health/features" -TimeoutSec 10).features
Assert-Equal $pwsFeatures.plenionEnableTimeControlPerformanceCorrectionEndpoint $true `
    'PWS TimeControl-correctieendpoint is niet actief.'

[pscustomobject]@{
    Service = $service.Status
    Listener = '127.0.0.1:5260'
    Health = 'ok'
    CockpitHttp = $cockpit.StatusCode
    Database = $database
    PwsHealth = $pwsHealth.status
    TimeControlCorrectionWrites = $true
    PwsCorrectionEndpoint = $true
    CloudflareAccess = $true
    Month = $Month
}
