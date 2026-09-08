#Requires -Version 5.1
<#
.SYNOPSIS
  Publish and activate a TimeControl release with fail-closed Production config checks.

.DESCRIPTION
  Required invariant: NO VALID PRODUCTION CONFIG => NO RELEASE ACTIVATION.

  Before switching `current`, this script verifies:
  - protected config\appsettings.Production.json exists
  - hardlink into the release succeeded
  - required PayrollShadow / PayrollActions flags are present

  Activation is refused if any of the above fail.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "C:\Dev\thebelgian-time-control",
    [string]$AppRoot = "C:\Apps\TheBelgian.TimeControl",
    [switch]$SkipTests,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-ProductionConfigReady {
    param(
        [Parameter(Mandatory)] [string]$ProtectedConfigPath,
        [Parameter(Mandatory)] [string]$ReleaseConfigPath
    )

    if (-not (Test-Path -LiteralPath $ProtectedConfigPath)) {
        throw "BLOCKED: protected Production config missing: $ProtectedConfigPath"
    }

    if (-not (Test-Path -LiteralPath $ReleaseConfigPath)) {
        throw "BLOCKED: release appsettings.Production.json missing (hardlink failed): $ReleaseConfigPath"
    }

    # Must be the same file identity (hardlink), not a stale publish copy.
    $protectedItem = Get-Item -LiteralPath $ProtectedConfigPath
    $releaseItem = Get-Item -LiteralPath $ReleaseConfigPath
    if ($protectedItem.Length -ne $releaseItem.Length) {
        throw "BLOCKED: release Production.json size does not match protected config (hardlink likely failed)"
    }

    $links = fsutil hardlink list $ReleaseConfigPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "BLOCKED: unable to verify hardlink for $ReleaseConfigPath : $links"
    }

    $linkText = ($links | Out-String)
    $protectedLeaf = [IO.Path]::GetFileName($ProtectedConfigPath)
    if ($linkText -notmatch [regex]::Escape("\config\$protectedLeaf") -and
        $linkText -notmatch [regex]::Escape($ProtectedConfigPath)) {
        throw "BLOCKED: release Production.json is not hardlinked to protected config. fsutil:`n$linkText"
    }

    $cfg = Get-Content -LiteralPath $ProtectedConfigPath -Raw | ConvertFrom-Json
    if ($null -eq $cfg.PayrollShadow) { throw "BLOCKED: PayrollShadow section missing in Production config" }
    if ($cfg.PayrollShadow.Enabled -ne $true) { throw "BLOCKED: PayrollShadow.Enabled must be true" }
    if ($cfg.PayrollShadow.AdminUiEnabled -ne $true) { throw "BLOCKED: PayrollShadow.AdminUiEnabled must be true" }
    if ($null -eq $cfg.PayrollActions) { throw "BLOCKED: PayrollActions section missing in Production config" }
    if ($cfg.PayrollActions.Enabled -ne $true) { throw "BLOCKED: PayrollActions.Enabled must be true" }

    Write-Host "Production config preflight OK (hardlink + PayrollShadow flags)."
}

Push-Location $RepoRoot
try {
    $sha = (git rev-parse HEAD).Trim()
    Write-Host "SHA=$sha"

    if (-not $SkipTests) {
        dotnet build "$RepoRoot\src\TheBelgian.TimeControl.Web\TheBelgian.TimeControl.Web.csproj" -c Release -warnaserror --nologo
        if ($LASTEXITCODE -ne 0) { throw "build failed" }
    }

    $releaseRoot = Join-Path $AppRoot "releases"
    $release = Join-Path $releaseRoot $sha
    if (Test-Path -LiteralPath $release) {
        throw "Release already exists: $release"
    }

    if ($DryRun) {
        Write-Host "DryRun: would publish to $release"
        return
    }

    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    dotnet publish "$RepoRoot\src\TheBelgian.TimeControl.Web\TheBelgian.TimeControl.Web.csproj" -c Release -o $release --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    $protected = Join-Path $AppRoot "config\appsettings.Production.json"
    $releaseProd = Join-Path $release "appsettings.Production.json"
    if (Test-Path -LiteralPath $releaseProd) {
        Remove-Item -LiteralPath $releaseProd -Force
    }

    if (-not (Test-Path -LiteralPath $protected)) {
        Remove-Item -LiteralPath $release -Recurse -Force
        throw "BLOCKED: protected Production config missing; release folder removed without activation"
    }

    New-Item -ItemType HardLink -Path $releaseProd -Target $protected | Out-Null
    Assert-ProductionConfigReady -ProtectedConfigPath $protected -ReleaseConfigPath $releaseProd

    $sha | Set-Content -Path (Join-Path $release "DEPLOYED_COMMIT.txt") -NoNewline

    $current = Join-Path $AppRoot "current"
    $previous = Join-Path $AppRoot "current.previous"
    $next = Join-Path $AppRoot "current.next"
    if (Test-Path -LiteralPath $next) { cmd /c "rmdir `"$next`"" | Out-Null }

    # Stage next junction only after config preflight passed.
    cmd /c "mklink /J `"$next`" `"$release`"" | Out-Null

    # Re-check through staged path before flipping current.
    Assert-ProductionConfigReady -ProtectedConfigPath $protected -ReleaseConfigPath (Join-Path $next "appsettings.Production.json")

    if (Test-Path -LiteralPath $previous) { cmd /c "rmdir `"$previous`"" | Out-Null }
    if (Test-Path -LiteralPath $current) {
        $oldTarget = (Get-Item -LiteralPath $current).Target
        if ($oldTarget) {
            cmd /c "mklink /J `"$previous`" `"$oldTarget`"" | Out-Null
        }
        cmd /c "rmdir `"$current`"" | Out-Null
    }

    cmd /c "mklink /J `"$current`" `"$release`"" | Out-Null
    cmd /c "rmdir `"$next`"" | Out-Null

    # Post-activation invariant
    Assert-ProductionConfigReady -ProtectedConfigPath $protected -ReleaseConfigPath (Join-Path $current "appsettings.Production.json")

    Restart-Service TheBelgian.TimeControl -Force
    (Get-Service TheBelgian.TimeControl).WaitForStatus("Running", [TimeSpan]::FromSeconds(45))
    Start-Sleep -Seconds 3
    $health = Invoke-WebRequest -Uri "http://127.0.0.1:5260/health" -UseBasicParsing -TimeoutSec 20
    if ($health.StatusCode -ne 200) { throw "Health check failed: $($health.StatusCode)" }

    # /Admin/Payroll must not be anonymous 404 from disabled PayrollShadow.
    try {
        Invoke-WebRequest -Uri "http://127.0.0.1:5260/Admin/Payroll" -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 15 | Out-Null
    }
    catch {
        $code = [int]$_.Exception.Response.StatusCode
        if ($code -eq 404) {
            throw "BLOCKED post-activate: /Admin/Payroll returned 404 (PayrollShadow likely not loaded)"
        }
        # 401/302 expected without Cloudflare Access token.
        Write-Host "Payroll root status=$code (404 would fail; auth redirect/challenge OK)"
    }

    Write-Host "DEPLOY_OK sha=$sha health=200"
}
finally {
    Pop-Location
}
