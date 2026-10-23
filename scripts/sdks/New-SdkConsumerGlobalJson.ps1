#Requires -Version 7.0
<#
.SYNOPSIS
    Writes isolated consumer SDK pins for packages available in a prepared feed.

.DESCRIPTION
    Copies the repository's .NET SDK settings and pins the requested Cohesion SDKs at
    the package version under test. Root global.json does not need Cohesion SDK pins.
    ApplicationModel is an implicit import and should not be included in SdkPackageId.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $PackageDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]] $SdkPackageId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory,

    [string] $RepositoryDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $RepositoryDirectory) {
    $RepositoryDirectory = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

$sdkVersions = [ordered]@{}
foreach ($packageId in $SdkPackageId) {
    $packagePath = Join-Path $PackageDirectory "$packageId.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Consumer SDK '$packageId' at version '$Version' was not produced in '$PackageDirectory'."
    }
    $sdkVersions[$packageId] = $Version
}

$repositoryGlobalJson = Get-Content -LiteralPath (Join-Path $RepositoryDirectory 'global.json') -Raw |
    ConvertFrom-Json
$consumerGlobalJson = [ordered]@{
    sdk = $repositoryGlobalJson.sdk
    'msbuild-sdks' = $sdkVersions
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$consumerGlobalJson |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'global.json') -Encoding utf8
