#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot cleanup of stale Cohesion SDK resolver registrations from the
    previous resolver-based architecture.

.DESCRIPTION
    The repo no longer ships a custom MSBuild SDK resolver -- distribution is
    via NuGet + global.json. Any registrations the old Install-Local.ps1 wrote
    into <dotnet>\sdk\<ver>\SdkResolvers\Assimalign.Cohesion.SdkResolver\ or
    into Visual Studio's MSBuild\Current\Bin\SdkResolvers\Assimalign.Cohesion.SdkResolver\
    point at DLLs that either don't exist or have the wrong shape, and will
    surface as confusing "exception of type System.Exception was thrown" or
    "MSB4247" errors.

    Run this once after pulling the pivot to remove them. It needs admin to
    write under Program Files. After this, no further registrations exist;
    SDK resolution flows through the standard NuGet resolver and global.json.

    Safe to run multiple times. Safe to run if nothing was ever registered.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$resolverName = 'Assimalign.Cohesion.SdkResolver'
$removed = 0

Write-Host "Removing stale Cohesion SDK resolver registrations..." -ForegroundColor Cyan

# 1) Every installed .NET SDK.
$sdkLines = & dotnet --list-sdks 2>$null
if ($sdkLines) {
    foreach ($line in $sdkLines) {
        if ($line -notmatch '^(\S+)\s+\[(.+)\]$') { continue }
        $sdkVersion = $Matches[1]
        $sdkBase = $Matches[2]
        $resolverDir = Join-Path $sdkBase "$sdkVersion\SdkResolvers\$resolverName"
        if (Test-Path -LiteralPath $resolverDir) {
            try {
                Remove-Item -LiteralPath $resolverDir -Recurse -Force -ErrorAction Stop
                Write-Host "  removed dotnet SDK $sdkVersion" -ForegroundColor DarkGray
                $removed++
            }
            catch {
                Write-Host "  could not remove '$resolverDir' - $($_.Exception.Message)" -ForegroundColor DarkYellow
            }
        }
    }
}

# 2) Every Visual Studio install discoverable via vswhere.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    try {
        $installs = & $vswhere -all -prerelease -products * -format json 2>$null | ConvertFrom-Json
        foreach ($vs in $installs) {
            $resolverDir = Join-Path $vs.installationPath "MSBuild\Current\Bin\SdkResolvers\$resolverName"
            if (Test-Path -LiteralPath $resolverDir) {
                try {
                    Remove-Item -LiteralPath $resolverDir -Recurse -Force -ErrorAction Stop
                    Write-Host "  removed VS $($vs.displayName)" -ForegroundColor DarkGray
                    $removed++
                }
                catch {
                    Write-Host "  could not remove '$resolverDir' - $($_.Exception.Message)" -ForegroundColor DarkYellow
                }
            }
        }
    }
    catch {
        Write-Host "  vswhere enumeration failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
}

# 3) Repo-root sdk-map.xml from the prior dev-loop.
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$mapPath = Join-Path $repoRoot 'sdk-map.xml'
if (Test-Path -LiteralPath $mapPath) {
    Remove-Item -LiteralPath $mapPath -Force
    Write-Host "  removed $mapPath" -ForegroundColor DarkGray
    $removed++
}

Write-Host ""
Write-Host "$removed registration(s) removed." -ForegroundColor Green
