#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Assimalign Cohesion MSI installer and renders the matching winget manifests.

.DESCRIPTION
    1. Compiles installer\Assimalign.Cohesion.Installer.wixproj into an MSI that installs
       the contents of the repo's `_out` folder to C:\Programs\cohesion.

    2. Extracts the freshly-built MSI's ProductCode and SHA256, then renders the three
       winget manifest templates (version / installer / locale) into
       installer\manifests\<Version>\.

    The MSI supports silent install via:
        msiexec /i Assimalign.Cohesion.msi /qn
    or via winget:
        winget install --manifest installer\manifests\<Version>

.PARAMETER Configuration
    MSBuild configuration. Defaults to Release.

.PARAMETER Version
    Product version to embed in the MSI and manifests. Defaults to 9.0.0.

.PARAMETER InstallerUrl
    The URL where the MSI will be downloaded from once published (used in the winget
    installer manifest). Defaults to a versioned GitHub release URL.

.EXAMPLE
    .\installer\build.ps1
    .\installer\build.ps1 -Version 9.0.1 -InstallerUrl 'https://example.com/Cohesion-9.0.1.msi'
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '9.0.0',
    [string]$InstallerUrl = ''
)

$ErrorActionPreference = 'Stop'

$installerRoot = $PSScriptRoot
$repoRoot      = Split-Path -Parent $installerRoot
$payloadDir    = Join-Path $repoRoot '_out'
$wixProj       = Join-Path $installerRoot 'Assimalign.Cohesion.Installer.wixproj'
$outputDir     = Join-Path $installerRoot "bin\$Configuration"
$templatesDir  = Join-Path $installerRoot 'manifests\templates'
$manifestsDir  = Join-Path $installerRoot "manifests\$Version"

if (-not $InstallerUrl) {
    $InstallerUrl = "https://github.com/assimalign/cohesion/releases/download/v$Version/Assimalign.Cohesion.msi"
}

if (-not (Test-Path -LiteralPath $payloadDir)) {
    throw "Payload directory '$payloadDir' does not exist. Build the repo first to populate '_out'."
}

# Stale diagnostic log can be hundreds of MB. Warn — WiX excludes it via folder selection.
$diagLog = Join-Path $payloadDir 'root-restore-diag.log'
if (Test-Path -LiteralPath $diagLog) {
    $size = (Get-Item -LiteralPath $diagLog).Length / 1MB
    Write-Host ("Note: root-restore-diag.log ({0:N1} MB) is excluded from the MSI." -f $size) -ForegroundColor DarkYellow
}

#region 1. Build the MSI ------------------------------------------------------

Write-Host "Restoring WiX project..." -ForegroundColor Cyan
& dotnet restore $wixProj
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

Write-Host "Building MSI ($Configuration, version $Version)..." -ForegroundColor Cyan
& dotnet build $wixProj `
    -c $Configuration `
    -p:ProductVersion=$Version `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

$msi = Get-ChildItem -LiteralPath $outputDir -Filter '*.msi' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $msi) {
    throw "Build completed but no .msi was produced under '$outputDir'."
}

#endregion

#region 2. Extract MSI metadata ----------------------------------------------

function Get-MsiProperties {
    param([Parameter(Mandatory)] [string]$MsiPath, [Parameter(Mandatory)] [string[]]$Properties)

    # Note: the WindowsInstaller COM API rejects multi-column SELECTs through InvokeMember
    # ("OpenView,Sql" exception). Querying each property in a separate single-column SELECT
    # is reliable.
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))
    $result = @{}
    foreach ($prop in $Properties) {
        $sql = "SELECT Value FROM Property WHERE Property = '$prop'"
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($rec) {
            $result[$prop] = $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, 1)
        }
    }
    return $result
}

$props = Get-MsiProperties -MsiPath $msi.FullName -Properties @('ProductCode','UpgradeCode','ProductVersion')
$productCode = $props['ProductCode']
if (-not $productCode) { throw "Could not extract ProductCode from MSI." }

$sha256 = (Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256).Hash
$sizeMb = [math]::Round($msi.Length / 1MB, 2)

#endregion

#region 3. Render winget manifests -------------------------------------------

if (-not (Test-Path -LiteralPath $templatesDir)) {
    Write-Warning "Manifest templates not found at '$templatesDir'. Skipping manifest generation."
}
else {
    if (Test-Path -LiteralPath $manifestsDir) {
        Remove-Item -LiteralPath $manifestsDir -Recurse -Force
    }
    [void](New-Item -ItemType Directory -Path $manifestsDir -Force)

    $releaseDate = (Get-Date).ToString('yyyy-MM-dd')

    $tokens = @{
        'Version'      = $Version
        'ProductCode'  = $productCode
        'Sha256'       = $sha256
        'InstallerUrl' = $InstallerUrl
        'ReleaseDate'  = $releaseDate
    }

    Get-ChildItem -LiteralPath $templatesDir -Filter '*.template' | ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        foreach ($key in $tokens.Keys) {
            $content = $content.Replace('{{' + $key + '}}', $tokens[$key])
        }
        $outName = $_.Name -replace '\.template$', ''
        $outPath = Join-Path $manifestsDir $outName
        # winget expects UTF-8 with no BOM.
        [System.IO.File]::WriteAllText($outPath, $content, (New-Object System.Text.UTF8Encoding $false))
        Write-Host "  wrote $outPath" -ForegroundColor DarkGray
    }
}

#endregion

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host ("  MSI:         {0}" -f $msi.FullName)
Write-Host ("  Size:        {0} MB" -f $sizeMb)
Write-Host ("  SHA256:      {0}" -f $sha256)
Write-Host ("  ProductCode: {0}" -f $productCode)
Write-Host ("  Manifests:   {0}" -f $manifestsDir)
Write-Host ""
Write-Host "Install (admin shell):"
Write-Host "  msiexec /i `"$($msi.FullName)`" /qn"
Write-Host ""
Write-Host "Install via winget (local manifest):"
Write-Host "  winget install --manifest `"$manifestsDir`""
Write-Host ""
Write-Host "Uninstall:"
Write-Host "  msiexec /x `"$($msi.FullName)`" /qn"
Write-Host "  winget uninstall Assimalign.Cohesion"
