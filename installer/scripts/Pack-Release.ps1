#Requires -Version 5.1
<#
.SYNOPSIS
    Produces the complete, version-consistent Cohesion NuGet release set.

.DESCRIPTION
    Strictly packs every shipping library and resource, every SDK pack, and every shared-framework
    targeting and runtime pack into _out/release/packages, at exactly one version, and validates
    the result against the authoritative inventory in
    installer/scripts/modules/CohesionPackaging.psm1.

    Three things separate this from the local dogfooding packer
    (installer/scripts/Install-Local.ps1):

      * It is strict. Install-Local.ps1 offers -ContinueOnLibraryError so a work-in-progress
        library cannot block the local feed. A release has no such mode: a project that does not
        build does not ship, and the run turns red.

      * The version is an argument, not a derivation. Install-Local.ps1 reads whatever
        build/Targets/Build.Version.props currently says. Here the caller passes the version the
        release tag asserted, and every layer of the version props is pinned to it by global
        MSBuild property - the layering means a stray SDK default otherwise lands a package at
        1.0.0 (see frameworks/Directory.Build.props for that exact bug).

      * It emits a publication manifest. package-order.txt and checksums.sha256 are what the
        publish jobs in .github/workflows/release.yml consume; they never check out the
        repository, so the producer has to hand them the file list and the hashes.

    Roughly 300 packages are produced (131 libraries and resources, 19 SDK packs, 19 targeting
    packs, and 19 x 7 runtime packs). Expect a long run.

.PARAMETER Version
    The SemVer package version to produce, for example 10.0.1 or 10.0.1-preview.2. Must match the
    canonical CohesionVersion; .github/workflows/release.yml enforces that against the release tag
    before calling this script.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER RuntimeIdentifier
    RIDs to build framework runtime packs for. Defaults to the full release set declared by the
    packaging module. Every value must also appear on the SDK's KnownFrameworkReference
    RuntimePackRuntimeIdentifiers, or consumers cannot resolve the pack.

.PARAMETER PackageDirectory
    Overrides the output directory. Defaults to _out/release/packages under the repository root.

.EXAMPLE
    ./installer/scripts/Pack-Release.ps1 -Version 10.0.1-preview.2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string[]] $RuntimeIdentifier,

    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Version
# ---------------------------------------------------------------------------------------------

$versionPattern = '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<suffix>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
$versionMatch = [System.Text.RegularExpressions.Regex]::Match(
    $Version,
    $versionPattern,
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $versionMatch.Success) {
    throw "Version '$Version' must be SemVer in the supported form MAJOR.MINOR.PATCH[-PRERELEASE]."
}
if ($versionMatch.Groups['suffix'].Success) {
    foreach ($identifier in $versionMatch.Groups['suffix'].Value.Split('.')) {
        if ([string]::IsNullOrWhiteSpace($identifier)) {
            throw "Version '$Version' contains an empty prerelease identifier."
        }
        if ($identifier -match '^[0-9]+$' -and $identifier -notmatch '^(0|[1-9][0-9]*)$') {
            throw "Version '$Version' contains a numeric prerelease identifier with a leading zero."
        }
    }
}

$majorVersion = $versionMatch.Groups['major'].Value
$minorVersion = $versionMatch.Groups['minor'].Value
$patchCoreVersion = $versionMatch.Groups['patch'].Value
$versionSuffix = $versionMatch.Groups['suffix'].Value
$versionPrefix = "$majorVersion.$minorVersion.$patchCoreVersion"

# build/Targets/Build.Version.props treats CohesionPatchVersion as the patch number PLUS any
# prerelease tag ("1-preview.2"), splitting the numeric core back out for AssemblyVersion. Feed it
# the same shape so the props file's own derivation stays self-consistent.
$patchVersion = $patchCoreVersion
if (-not [string]::IsNullOrWhiteSpace($versionSuffix)) {
    $patchVersion = "$patchCoreVersion-$versionSuffix"
}

# ---------------------------------------------------------------------------------------------
# Paths and inventory
# ---------------------------------------------------------------------------------------------

$repositoryDirectory = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent | Split-Path -Parent))

Import-Module (Join-Path $PSScriptRoot 'modules/CohesionPackaging.psm1') -Force
Assert-CohesionReleaseInventory -RepositoryDirectory $repositoryDirectory

if (-not $RuntimeIdentifier -or $RuntimeIdentifier.Count -eq 0) {
    $RuntimeIdentifier = Get-CohesionReleaseRuntimeIdentifier
}

$releaseOutputDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryDirectory '_out/release'))
if (-not $PackageDirectory) {
    $PackageDirectory = Join-Path $releaseOutputDirectory 'packages'
}
# Resolved through the PowerShell provider, not [System.IO.Path]::GetFullPath: the latter anchors
# a relative path to the process working directory rather than to $PWD, and this path is about to
# have files deleted out of it.
$packageDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackageDirectory)

# The directory is emptied of package artifacts below. Refuse to do that anywhere but under the
# release output root, so a mistyped -PackageDirectory cannot delete a real folder.
$releaseOutputPrefix = $releaseOutputDirectory.TrimEnd(
    [char[]] @(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $packageDirectory.StartsWith($releaseOutputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The release package directory must remain inside $releaseOutputDirectory."
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

# Stale artifacts from an earlier run would sail past a per-project exit-code check and land in
# the published set, so clear them before packing rather than trusting the pack to overwrite.
Get-ChildItem -LiteralPath $packageDirectory -File |
    Where-Object {
        $_.Name.EndsWith('.nupkg', [System.StringComparison]::OrdinalIgnoreCase) -or
        $_.Name.EndsWith('.snupkg', [System.StringComparison]::OrdinalIgnoreCase) -or
        $_.Name -eq 'checksums.sha256' -or
        $_.Name -eq 'package-order.txt' -or
        $_.Name -eq 'symbol-package-order.txt'
    } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$repositoryCommit = $env:GITHUB_SHA
if ([string]::IsNullOrWhiteSpace($repositoryCommit)) {
    $repositoryCommit = (& git -C $repositoryDirectory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Resolving the repository commit failed.'
    }
}

# ---------------------------------------------------------------------------------------------
# Build properties
# ---------------------------------------------------------------------------------------------

# Passed on the command line, which makes them GLOBAL properties: a <PropertyGroup> in any
# Directory.Build.props cannot override them. That is the point - the version reaches every one of
# the layered props files (build/Targets/Build.Version.props, libraries/, frameworks/, sdks/)
# identically, instead of each re-deriving it and one of them getting it wrong.
$buildProperties = @(
    "-p:CohesionMajorVersion=$majorVersion"
    "-p:CohesionMinorVersion=$minorVersion"
    "-p:CohesionPatchVersion=$patchVersion"
    "-p:CohesionVersionPrefix=$versionPrefix"
    "-p:CohesionVersionSuffix=$versionSuffix"
    "-p:CohesionVersion=$Version"
    "-p:VersionPrefix=$versionPrefix"
    "-p:VersionSuffix=$versionSuffix"
    "-p:PackageVersion=$Version"
    '-p:ContinuousIntegrationBuild=true'
    "-p:RepositoryCommit=$repositoryCommit"
    "-p:PackageOutputPath=$packageDirectory"
)

# Deliberately NOT -warnaserror. Viu's release packer uses it because Viu's tree is warning-clean;
# this one is not yet, and a release packer is the wrong place to introduce that gate. Adopt it
# when .github/actions/build adopts it, not before.

Write-Host "Packing Cohesion $Version from $repositoryCommit" -ForegroundColor Cyan
Write-Host "  Configuration : $Configuration"
Write-Host "  RIDs          : $($RuntimeIdentifier -join ', ')"
Write-Host "  Output        : $packageDirectory"
Write-Host ""

# ---------------------------------------------------------------------------------------------
# Prerequisite: the custom build tasks
# ---------------------------------------------------------------------------------------------

# build/Build.props declares a <UsingTask> pointing at the compiled tasks assembly, which every
# code-generating library needs. On a fresh checkout - which is exactly what a release runner is -
# the DLL does not exist yet and every subsequent pack fails with MSB4062.
$buildTasksProject = Join-Path $repositoryDirectory 'build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj'
Write-Host 'Building the Cohesion build tasks' -ForegroundColor Green
& dotnet build $buildTasksProject --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Building $buildTasksProject failed with exit code $LASTEXITCODE."
}

# ---------------------------------------------------------------------------------------------
# Pack
# ---------------------------------------------------------------------------------------------

function Invoke-PackageBuild {
    param(
        [Parameter(Mandatory)]
        [string] $Project,

        [string[]] $AdditionalArguments = @()
    )

    & dotnet pack $Project `
        --configuration $Configuration `
        --nologo `
        @buildProperties `
        @AdditionalArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Packing $Project failed with exit code $LASTEXITCODE."
    }
}

$plan = @(Get-CohesionReleaseProject -RepositoryDirectory $repositoryDirectory -RuntimeIdentifier $RuntimeIdentifier)
$planIndex = 0

foreach ($item in $plan) {
    $planIndex++
    Write-Host ("[{0}/{1}] {2}" -f $planIndex, $plan.Count, $item.PackageId) -ForegroundColor Green

    if (-not (Test-Path -LiteralPath $item.ProjectPath -PathType Leaf)) {
        throw "The release inventory names a project that does not exist: $($item.ProjectPath)"
    }

    switch ($item.Kind) {
        'FrameworkRuntime' {
            Invoke-PackageBuild `
                -Project $item.ProjectPath `
                -AdditionalArguments @("-p:RuntimeIdentifier=$($item.RuntimeIdentifier)")
        }

        'Sdk' {
            # sdks/Directory.Build.targets packs the SDK task closure by globbing $(OutDir)**\*.
            # Anything a previous build left in that folder - a renamed dependency, a dropped
            # analyzer - is swept into the package silently. Cleaning first is what keeps an SDK
            # pack a function of the current source tree.
            & dotnet clean $item.ProjectPath --configuration $Configuration --nologo
            if ($LASTEXITCODE -ne 0) {
                throw "Cleaning $($item.ProjectPath) failed with exit code $LASTEXITCODE."
            }

            Invoke-PackageBuild -Project $item.ProjectPath
        }

        default {
            Invoke-PackageBuild -Project $item.ProjectPath
        }
    }
}

# ---------------------------------------------------------------------------------------------
# Validate and manifest
# ---------------------------------------------------------------------------------------------

$expectedPackageId = @($plan | ForEach-Object PackageId)
Assert-CohesionReleasePackageSet `
    -PackageDirectory $packageDirectory `
    -Version $Version `
    -ExpectedPackageId $expectedPackageId

# The set being right says nothing about the packages being right. This opens each archive and
# checks the branding actually landed - the icon is wired centrally through an MSBuild import
# chain, and a project can fall out of that chain and still pack cleanly.
$packageIcon = (
    & dotnet msbuild (Join-Path $repositoryDirectory 'build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj') `
        -nologo -getProperty:PackageIcon
) -join ''
if ($LASTEXITCODE -ne 0) {
    throw "Reading the canonical PackageIcon failed with exit code $LASTEXITCODE."
}
$packageIcon = $packageIcon.Trim()
if (-not $packageIcon) {
    throw 'build/Targets/Build.Branding.props declares no PackageIcon.'
}

Assert-CohesionPackageMetadata `
    -PackageDirectory $packageDirectory `
    -ExpectedIcon $packageIcon

$expectedPackageFile = @($expectedPackageId | ForEach-Object { "$_.$Version.nupkg" })

# Symbol packages: Release builds currently set DebugType=none repo-wide
# (build/Targets/Build.Global.props), so no PDBs and therefore no .snupkg are produced. Collect
# whatever is there anyway rather than asserting zero - turning symbols on should extend the
# manifest, not break the packer.
$actualSymbolPackageFile = @(
    Get-ChildItem -LiteralPath $packageDirectory -Filter '*.snupkg' -File |
        ForEach-Object Name |
        Sort-Object
)

# package-order.txt is the publication manifest the publish jobs iterate. The order is the pack
# plan's order - libraries and resources, then framework targeting packs, then framework runtime
# packs, then SDK packs - which is deterministic and mirrors the dependency direction of the
# distribution shapes. It is not a topological sort of the library graph; NuGet does not resolve
# dependencies at push time, so push order carries no correctness requirement.
$packageOrderPath = Join-Path $packageDirectory 'package-order.txt'
$expectedPackageFile | Set-Content -LiteralPath $packageOrderPath -Encoding utf8

# Written with -Value rather than through the pipeline: piping an EMPTY array to Set-Content is a
# no-op, so the pipeline form silently produces no file at all in the normal case (no symbols
# today), and a consumer reading it would get "file not found" instead of "nothing to publish".
$symbolPackageOrderPath = Join-Path $packageDirectory 'symbol-package-order.txt'
Set-Content -LiteralPath $symbolPackageOrderPath -Value $actualSymbolPackageFile -Encoding utf8

# ASCII, two spaces, lowercase hex: the exact format `sha256sum --check` expects. The publish jobs
# re-verify the artifact against this file, which is what makes "the thing we published is the
# thing we validated" a checked claim rather than an assumption about artifact storage.
$checksumPath = Join-Path $packageDirectory 'checksums.sha256'
$checksumLine = foreach ($packageFile in @($expectedPackageFile + $actualSymbolPackageFile)) {
    $packagePath = Join-Path $packageDirectory $packageFile
    $packageHash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
    "$($packageHash.Hash.ToLowerInvariant())  $packageFile"
}
$checksumLine | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host ""
Write-Host ("Created {0} packages and {1} symbol packages in {2}" -f
    $expectedPackageFile.Count,
    $actualSymbolPackageFile.Count,
    $packageDirectory) -ForegroundColor Cyan
