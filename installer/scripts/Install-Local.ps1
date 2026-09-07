#Requires -Version 5.1
<#
.SYNOPSIS
    Packs the Cohesion SDKs, the App targeting pack, and the App runtime
    pack(s) into the repo's local NuGet feed (_out/packages) so any consumer
    csproj under this repo can restore them via the nuget.config + global.json
    wiring at the repo root.

.DESCRIPTION
    Zero system-wide registration. Zero MSBuild SDK resolver. Zero admin.

    What this script produces in _out/packages/, in three distribution shapes:

        SDK packs (consumed via <Project Sdk="Assimalign.Cohesion.Sdk[.X]">):
            Assimalign.Cohesion.Sdk.<ver>.nupkg
            Assimalign.Cohesion.Sdk.<Domain>.<ver>.nupkg

        Framework packs (auto-included by the SDK via <FrameworkReference>):
            Assimalign.Cohesion.App[.<Domain>].Ref.<ver>.nupkg              (targeting pack)
            Assimalign.Cohesion.App[.<Domain>].Runtime.<rid>.<ver>.nupkg    (one per RID)

        Individual library packs (consumed directly via <PackageReference>):
            Assimalign.Cohesion.Core.<ver>.nupkg
            Assimalign.Cohesion.<Library>.<ver>.nupkg
            ... one per project in the curated release inventory

    Local packages append a final .local prerelease identifier to the canonical
    version (for example, 10.0.1-preview.3.local). They are never release artifacts.
    The explicit release-validation mode instead uses the canonical identity on a
    clean runner and is restricted to the SDK/framework bootstrap closure.

    SDK-path consumers write <Project Sdk="Assimalign.Cohesion.Sdk"> and the
    SDK auto-includes <FrameworkReference Include="Assimalign.Cohesion.App" />,
    which the SDK's KnownFrameworkReference machinery resolves by pulling the
    targeting pack at compile time and (for self-contained builds) the runtime
    pack at publish time. Direct-reference consumers bypass the SDK and pull a
    single library via <PackageReference>.

.PARAMETER Configuration
    Debug or Release. Defaults to Debug.

.PARAMETER Rids
    RIDs to build runtime packs for. Defaults to the host RID only. Pass
    e.g. -Rids 'win-x64','linux-x64','osx-arm64' to build cross-RID.

.PARAMETER SkipSdks
    Skip rebuilding the SDK packages. Useful when iterating only on framework
    or library code.

.PARAMETER SkipFramework
    Skip rebuilding the framework packs. Useful when iterating only on SDK
    targets or props.

.PARAMETER SkipLibraries
    Skip rebuilding the individual library + resource packs. Useful when
    iterating only on SDK / framework wiring.

.PARAMETER ContinueOnLibraryError
    When set, library/resource pack failures in section [5/5] are collected
    and reported at the end instead of aborting the run. Useful for landing
    the working subset of the feed while WIP libraries don't compile.

.PARAMETER UseCanonicalVersion
    Packs the SDK/framework bootstrap closure at the exact canonical version, including a stable
    version. Reserved for clean release-validation runners; requires -SkipLibraries so ordinary
    local development cannot replace shipping library identities in the global NuGet cache.

.EXAMPLE
    pwsh installer\scripts\Install-Local.ps1
        Pack everything (host-RID runtime pack only) into _out/packages.

.EXAMPLE
    pwsh installer\scripts\Install-Local.ps1 -Rids 'win-x64','linux-x64' -Configuration Release
        Cross-RID release build.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',

    [string[]]$Rids,

    [switch]$SkipSdks,
    [switch]$SkipFramework,
    [switch]$SkipLibraries,

    # When set, library/resource pack failures in section [5/5] are reported
    # at the end instead of aborting the run. Useful while WIP libraries
    # don't compile - lets the rest of the feed land. Default off so CI
    # treats a broken library as a hard failure.
    [switch]$ContinueOnLibraryError,

    [switch]$UseCanonicalVersion,

    # Bypass the locked-cache check. Use only if you understand the risk:
    # the new .nupkg is still produced under _out/packages, but the cached
    # extract under ~/.nuget/packages won't be replaced, so consumer restores
    # will keep serving the OLD content until the lock releases or the cache
    # is pruned by hand.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$feedDir  = Join-Path $repoRoot '_out\packages'

if ($UseCanonicalVersion -and -not $SkipLibraries) {
    throw '-UseCanonicalVersion is a release-validation bootstrap and requires -SkipLibraries.'
}

# Resolve the canonical version, then derive a local-only prerelease above it. Local packages must
# never reuse the id/version of a published artifact. A stable canonical line is rejected because
# adding -local would sort below the stable release; the required post-tag bump must land first.
Import-Module (Join-Path $PSScriptRoot 'modules/CohesionLocalPackaging.psm1') -Force
$canonicalVersion = & (Join-Path $PSScriptRoot 'Get-CohesionVersion.ps1') -RepoRoot $repoRoot
$localVersion = Get-CohesionLocalPackageVersion `
    -Version $canonicalVersion `
    -UseCanonicalVersion:$UseCanonicalVersion
$cohesionVersion = $localVersion.Version

# Global MSBuild properties keep every package shape and every generated dependency version on the
# same local identity. This mirrors Pack-Release.ps1's explicit release-version vector.
$localPackProperties = @(
    "-p:CohesionMajorVersion=$($localVersion.MajorVersion)"
    "-p:CohesionMinorVersion=$($localVersion.MinorVersion)"
    "-p:CohesionPatchVersion=$($localVersion.PatchVersion)"
    "-p:CohesionVersionPrefix=$($localVersion.VersionPrefix)"
    "-p:CohesionVersionSuffix=$($localVersion.VersionSuffix)"
    "-p:CohesionVersion=$($localVersion.Version)"
    "-p:VersionPrefix=$($localVersion.VersionPrefix)"
    "-p:VersionSuffix=$($localVersion.VersionSuffix)"
    "-p:PackageVersion=$($localVersion.Version)"
    "-p:PackageOutputPath=$feedDir"
)

if (-not $Rids -or $Rids.Count -eq 0) {
    $Rids = @((& dotnet --info | Select-String -Pattern '^\s*RID:\s*(\S+)').Matches.Groups[1].Value)
    if (-not $Rids -or [string]::IsNullOrWhiteSpace($Rids[0])) {
        throw "Could not determine host RID from 'dotnet --info'. Pass -Rids explicitly."
    }
}

Write-Host "Cohesion local pack" -ForegroundColor Cyan
Write-Host "  Source version: $canonicalVersion"
Write-Host "  Package version: $cohesionVersion"
Write-Host "  Configuration : $Configuration"
Write-Host "  RIDs          : $($Rids -join ', ')"
Write-Host "  Repo root     : $repoRoot"
Write-Host "  Feed          : $feedDir"
Write-Host ""

# Same-version repack workaround. NuGet's global-packages cache caches the
# extracted contents of packages by id+version. Repacking a version from a
# changed source tree without bumping it means the consumer's restore keeps
# serving the OLD extract from ~/.nuget/packages/ instead of re-reading the
# fresh .nupkg in our local feed. Prune cached extracts up front so the next
# restore picks up the fresh package.
# The framework, SDK, and library/resource sets come from the shared packaging module, so the
# local dogfooding feed and the release set (installer/scripts/Pack-Release.ps1) cannot disagree
# about what exists. Adding a shipping package is one edit, in
# installer/scripts/modules/CohesionPackaging.psm1 - not two lists that drift.
#
# Each framework family has a Ref pack (one .nupkg) and a per-RID Runtime pack (one
# .nupkg per RID). Each SDK entry maps to sdks/<name>/Tasks/<name>.Tasks.csproj. The
# base Sdk comes first because the others chain to it. The module keeps both lists
# aligned with the folders under frameworks/ and sdks/ and with the
# KnownFrameworkReferences in
# sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props.
Import-Module (Join-Path $PSScriptRoot 'modules/CohesionPackaging.psm1') -Force
$cohesionFrameworks = Get-CohesionReleaseFramework
$cohesionSdks       = Get-CohesionReleaseSdk
$cohesionLibraries  = @(Get-CohesionReleaseLibrary -RepositoryDirectory $repoRoot)

$cohesionPackages = @(
    if (-not $SkipSdks) {
        $cohesionSdks | ForEach-Object { $_.ToLowerInvariant() }
    }
    if (-not $SkipFramework) {
        $cohesionFrameworks | ForEach-Object { "$($_.ToLowerInvariant()).ref" }
        $cohesionFrameworks | ForEach-Object {
            $frameworkPackage = $_.ToLowerInvariant()
            $Rids | ForEach-Object { "$frameworkPackage.runtime.$_" }
        }
    }
    if (-not $SkipLibraries) {
        $cohesionLibraries | ForEach-Object { $_.PackageId.ToLowerInvariant() }
    }
)

$globalPackagesRoot = & dotnet nuget locals global-packages --list 2>$null |
    ForEach-Object { ($_ -split ':\s*', 2)[-1].Trim() } |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
    Select-Object -First 1

# Locked-cache check. If a running MSBuild process (most often Visual Studio)
# has any cached Cohesion SDK Tasks DLL loaded, NuGet's restore can't replace
# the extract on disk and any subsequent consumer build fails with
# UnauthorizedAccessException. We probe the known-at-risk paths up front by
# trying to open each for exclusive write -- it's the same operation NuGet
# would attempt, so a successful probe means NuGet will also succeed.
if ($globalPackagesRoot -and -not $Force) {
    $tasksDlls = @($cohesionSdks | ForEach-Object {
        "$($_.ToLowerInvariant())\$cohesionVersion\Tasks\$_.Tasks.dll"
    })
    $lockedPaths = @()
    foreach ($rel in $tasksDlls) {
        $abs = Join-Path $globalPackagesRoot $rel
        if (-not (Test-Path -LiteralPath $abs)) { continue }
        try {
            $stream = [System.IO.File]::Open($abs, 'Open', 'Write', 'None')
            $stream.Close()
        }
        catch {
            $lockedPaths += $abs
        }
    }

    if ($lockedPaths.Count -gt 0) {
        Write-Host ""
        Write-Host "ERROR: Cached Cohesion SDK Tasks DLL is file-locked." -ForegroundColor Red
        foreach ($p in $lockedPaths) {
            Write-Host "  $p" -ForegroundColor Red
        }
        Write-Host ""
        $devenvProcs = Get-Process devenv -ErrorAction SilentlyContinue
        if ($devenvProcs) {
            Write-Host "Likely culprit: Visual Studio is running ($($devenvProcs.Count) instance(s))." -ForegroundColor Yellow
            Write-Host "Close all VS windows, then re-run this script." -ForegroundColor Yellow
        }
        else {
            Write-Host "No devenv.exe found, but something has the DLL loaded." -ForegroundColor Yellow
            Write-Host "Likely an IDE (Rider, etc.) or a standalone MSBuild process." -ForegroundColor Yellow
        }
        Write-Host ""
        Write-Host "To pack anyway (the new .nupkg will land in _out/packages but consumers" -ForegroundColor DarkGray
        Write-Host "will keep restoring the cached OLD content until the lock releases): pass -Force" -ForegroundColor DarkGray
        Write-Host ""
        throw "Aborted: cached SDK Tasks DLL is locked. See message above."
    }
}

if ($globalPackagesRoot) {
    foreach ($pkg in $cohesionPackages) {
        $cached = Join-Path (Join-Path $globalPackagesRoot $pkg) $cohesionVersion
        if (Test-Path -LiteralPath $cached) {
            Remove-Item -LiteralPath $cached -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

#region 1. Cohesion build tasks (prerequisite) -----------------------------
# build/Build.props has a <UsingTask> pointing at
# build/Tasks/bin/<cfg>/<tfm>/Assimalign.Cohesion.Build.Tasks.dll, which is
# referenced by every Cohesion library that uses code-generation (Resilience,
# etc.). Without this DLL on disk every subsequent dotnet pack fails with
# MSB4062 ("task could not be loaded"). Local devs typically already have it
# from previous builds; CI's fresh runner doesn't.
Write-Host "[1/5] Building Cohesion build tasks..." -ForegroundColor Cyan
$buildTasksProj = Join-Path $repoRoot 'build\Tasks\Assimalign.Cohesion.Build.Tasks.csproj'
Write-Host "  build $buildTasksProj" -ForegroundColor DarkGray
& dotnet build $buildTasksProj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $buildTasksProj" }
#endregion

#region 2. SDK packs --------------------------------------------------------
if (-not $SkipSdks) {
    Write-Host "[2/5] Packing SDK projects..." -ForegroundColor Cyan
    # Each entry in $cohesionSdks maps to sdks/<SdkName>/Tasks/<SdkName>.Tasks.csproj.
    # Resource-domain SDKs are scaffolded by New-CohesionDomainScaffold.ps1; each
    # corresponds to a folder under resources/ and a framework family in frameworks/.
    $sdkProjects = $cohesionSdks | ForEach-Object { Join-Path $repoRoot "sdks\$_\Tasks\$_.Tasks.csproj" }
    foreach ($proj in $sdkProjects) {
        if (-not (Test-Path -LiteralPath $proj)) {
            Write-Host "  (skip, not found) $proj" -ForegroundColor DarkGray
            continue
        }
        Write-Host "  pack $proj" -ForegroundColor DarkGray
        & dotnet pack $proj -c $Configuration --nologo @localPackProperties
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $proj" }
    }
}
else {
    Write-Host "[2/5] Skipping SDK packs (-SkipSdks)." -ForegroundColor DarkYellow
}
#endregion

#region 3. Framework runtime packs (per framework x per RID) ---------------
if (-not $SkipFramework) {
    Write-Host "[3/5] Packing framework runtime pack(s)..." -ForegroundColor Cyan
    foreach ($framework in $cohesionFrameworks) {
        $runtimeProj = Join-Path $repoRoot "frameworks\$framework.Runtime\src\$framework.Runtime.csproj"
        if (-not (Test-Path -LiteralPath $runtimeProj)) {
            Write-Host "  (skip, not found) $runtimeProj" -ForegroundColor DarkGray
            continue
        }
        foreach ($rid in $Rids) {
            Write-Host "  pack $framework runtime ($rid)" -ForegroundColor DarkGray
            & dotnet pack $runtimeProj -c $Configuration -p:RuntimeIdentifier=$rid --nologo @localPackProperties
            if ($LASTEXITCODE -ne 0) { throw "Runtime pack failed for $framework / $rid" }
        }
    }
}
else {
    Write-Host "[3/5] Skipping runtime packs (-SkipFramework)." -ForegroundColor DarkYellow
}
#endregion

#region 4. Framework targeting packs (one per framework) -------------------
if (-not $SkipFramework) {
    Write-Host "[4/5] Packing framework targeting pack(s)..." -ForegroundColor Cyan
    foreach ($framework in $cohesionFrameworks) {
        $refsProj = Join-Path $repoRoot "frameworks\$framework.Refs\src\$framework.Refs.csproj"
        if (-not (Test-Path -LiteralPath $refsProj)) {
            Write-Host "  (skip, not found) $refsProj" -ForegroundColor DarkGray
            continue
        }
        Write-Host "  pack $framework refs" -ForegroundColor DarkGray
        & dotnet pack $refsProj -c $Configuration --nologo @localPackProperties
        if ($LASTEXITCODE -ne 0) { throw "Targeting pack failed for $framework" }
    }
}
else {
    Write-Host "[4/5] Skipping targeting pack (-SkipFramework)." -ForegroundColor DarkYellow
}
#endregion

#region 5. Individual library + resource packs ----------------------------
# Each library/resource csproj is its own NuGet package, addressed via
# <PackageReference Include="Assimalign.Cohesion.<X>" />. Distinct from the
# framework distribution path (Sdk + App.<Domain>.Ref/.Runtime) which bundles
# library assemblies into the framework packs - consumers who want a single
# library without the full SDK / framework reference live on this path.
#
# Historically this was implicit: <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
# on libraries/ + resources/ made each library auto-pack on Build, and the
# framework Refs/Runtime packs above triggered those builds transitively. That
# property is now removed because it races with the build output once any
# project's graph contains an analyzer-style ProjectReference (NU5026); see
# libraries/Directory.Build.props for the full reasoning. Packing the curated
# release inventory explicitly here is the replacement and keeps the local feed
# aligned with Pack-Release.ps1.
if (-not $SkipLibraries) {
    Write-Host "[5/5] Packing libraries + resources..." -ForegroundColor Cyan

    # A prior local run may have left a different version of a library in the flat feed. Remove
    # only complete package-id + SemVer matches from the curated library/resource inventory;
    # prefix-sharing package ids and SDK/framework packages remain untouched.
    New-Item -ItemType Directory -Path $feedDir -Force | Out-Null
    $staleLibraryPackage = @(
        Get-CohesionStaleLibraryPackage `
            -PackageDirectory $feedDir `
            -PackageId @($cohesionLibraries | ForEach-Object PackageId)
    )
    foreach ($packagePath in $staleLibraryPackage) {
        Remove-Item -LiteralPath $packagePath -Force
    }
    Write-Host ("  pruned {0} stale library/resource package(s) from the local feed" -f
        $staleLibraryPackage.Count) -ForegroundColor DarkGray

    $libraryProjects = @(
        $cohesionLibraries | ForEach-Object { Get-Item -LiteralPath $_.ProjectPath }
    ) | Sort-Object FullName
    Write-Host ("  found {0} project(s)" -f $libraryProjects.Count) -ForegroundColor DarkGray

    $libraryFailures = @()
    foreach ($p in $libraryProjects) {
        Write-Host "  pack $($p.FullName)" -ForegroundColor DarkGray
        & dotnet pack $p.FullName -c $Configuration --nologo @localPackProperties
        if ($LASTEXITCODE -ne 0) {
            if ($ContinueOnLibraryError) {
                $libraryFailures += $p.FullName
                Write-Host "    !! pack failed (continuing because -ContinueOnLibraryError)" -ForegroundColor DarkYellow
            }
            else {
                throw "dotnet pack failed for $($p.FullName)"
            }
        }
    }
    if ($libraryFailures.Count -gt 0) {
        Write-Host ""
        Write-Host ("[5/5] {0} library/resource pack(s) failed:" -f $libraryFailures.Count) -ForegroundColor DarkYellow
        $libraryFailures | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
    }
}
else {
    Write-Host "[5/5] Skipping library/resource packs (-SkipLibraries)." -ForegroundColor DarkYellow
}
#endregion

# The package-backed Database E2E sample must pin the exact SDK identity produced above. Keep the
# versionless template in source and generate global.json only after every requested pack has
# succeeded, so a failed pack never leaves a pin that names artifacts which do not exist.
if (-not $SkipSdks) {
    $consumerTemplatePath = Join-Path $repoRoot `
        'resources\Database\global.template.json'
    if (Test-Path -LiteralPath $consumerTemplatePath) {
        $consumerGlobalJsonPath = Join-Path (Split-Path -Parent $consumerTemplatePath) 'global.json'
        $consumerGlobalJson = Get-Content -LiteralPath $consumerTemplatePath -Raw | ConvertFrom-Json
        $sdkVersions = $consumerGlobalJson.PSObject.Properties['msbuild-sdks'].Value
        if ($null -eq $sdkVersions) {
            throw "Consumer SDK template '$consumerTemplatePath' has no msbuild-sdks object."
        }

        foreach ($sdk in $sdkVersions.PSObject.Properties) {
            $packagePath = Join-Path $feedDir "$($sdk.Name).$cohesionVersion.nupkg"
            if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
                throw "Consumer SDK '$($sdk.Name)' was not produced at '$packagePath'."
            }

            $sdk.Value = $cohesionVersion
        }

        $temporaryGlobalJsonPath = "$consumerGlobalJsonPath.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            $json = $consumerGlobalJson | ConvertTo-Json -Depth 20
            [System.IO.File]::WriteAllText(
                $temporaryGlobalJsonPath,
                $json + [Environment]::NewLine,
                [System.Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temporaryGlobalJsonPath -Destination $consumerGlobalJsonPath -Force
        }
        finally {
            if (Test-Path -LiteralPath $temporaryGlobalJsonPath) {
                Remove-Item -LiteralPath $temporaryGlobalJsonPath -Force
            }
        }

        Write-Host "  generated $consumerGlobalJsonPath ($cohesionVersion)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "Smoke-test it: build any consumer csproj using <Project Sdk=`"Assimalign.Cohesion.Sdk[.Domain]`">" -ForegroundColor DarkGray
Write-Host "with the SDK versions pinned in its global.json; the repo nuget.config resolves from _out/packages." -ForegroundColor DarkGray
