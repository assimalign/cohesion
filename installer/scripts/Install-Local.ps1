#Requires -Version 5.1
<#
.SYNOPSIS
    Packs the Cohesion SDKs, the App targeting pack, and the App runtime
    pack(s) into the repo's local NuGet feed (_out/packages) so any consumer
    csproj under this repo can restore them via the nuget.config + global.json
    wiring at the repo root.

.DESCRIPTION
    Zero system-wide registration. Zero MSBuild SDK resolver. Zero admin.

    What this script produces in _out/packages/:
        Assimalign.Cohesion.Sdk.<ver>.nupkg
        Assimalign.Cohesion.Sdk.Web.<ver>.nupkg
        Assimalign.Cohesion.Sdk.Database.<ver>.nupkg
        Assimalign.Cohesion.App.Ref.<ver>.nupkg              (targeting pack)
        Assimalign.Cohesion.App.Runtime.<rid>.<ver>.nupkg    (one per RID)

    Consumers then write <Project Sdk="Assimalign.Cohesion.Sdk"> and the SDK
    auto-includes <FrameworkReference Include="Assimalign.Cohesion.App" />,
    which the SDK's KnownFrameworkReference machinery resolves by pulling the
    targeting pack at compile time and (for self-contained builds) the runtime
    pack at publish time.

.PARAMETER Configuration
    Debug or Release. Defaults to Debug.

.PARAMETER Rids
    RIDs to build runtime packs for. Defaults to the host RID only. Pass
    e.g. -Rids 'win-x64','linux-x64','osx-arm64' to build cross-RID.

.PARAMETER SkipSdks
    Skip rebuilding the SDK packages. Useful when iterating only on framework
    code.

.PARAMETER SkipFramework
    Skip rebuilding the framework packs. Useful when iterating only on SDK
    targets or props.

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

# Resolve the Cohesion version. Delegated to a shared helper so the GitHub
# Actions workflow (.github/workflows/framework.yml) and this script can never
# drift on what they think the package version is. See Get-CohesionVersion.ps1
# for the derivation details — Build.TargetFramework.props remains the single
# source of truth for the major version.
$cohesionVersion = & (Join-Path $PSScriptRoot 'Get-CohesionVersion.ps1') -RepoRoot $repoRoot

if (-not $Rids -or $Rids.Count -eq 0) {
    $Rids = @((& dotnet --info | Select-String -Pattern '^\s*RID:\s*(\S+)').Matches.Groups[1].Value)
    if (-not $Rids -or [string]::IsNullOrWhiteSpace($Rids[0])) {
        throw "Could not determine host RID from 'dotnet --info'. Pass -Rids explicitly."
    }
}

Write-Host "Cohesion local pack" -ForegroundColor Cyan
Write-Host "  Version       : $cohesionVersion"
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
# Per-framework families: each has a Ref pack (one .nupkg) and a per-RID
# Runtime pack (one .nupkg per RID). Keep this list aligned with the
# framework projects under frameworks/ and the KnownFrameworkReferences
# in sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props.
$cohesionFrameworks = @(
    'Assimalign.Cohesion.App',
    'Assimalign.Cohesion.App.Web',
    'Assimalign.Cohesion.App.Database',
    'Assimalign.Cohesion.App.ApiManager',
    'Assimalign.Cohesion.App.ConfigurationStore',
    'Assimalign.Cohesion.App.EmailHub',
    'Assimalign.Cohesion.App.EventHub',
    'Assimalign.Cohesion.App.IdentityHub',
    'Assimalign.Cohesion.App.IoTHub',
    'Assimalign.Cohesion.App.LoadBalancer',
    'Assimalign.Cohesion.App.LogSpace',
    'Assimalign.Cohesion.App.MediaHub',
    'Assimalign.Cohesion.App.MessageHub',
    'Assimalign.Cohesion.App.NatGateway',
    'Assimalign.Cohesion.App.NotificationHub',
    'Assimalign.Cohesion.App.Rezolvr',
    'Assimalign.Cohesion.App.Scheduler',
    'Assimalign.Cohesion.App.SecretStore',
    'Assimalign.Cohesion.App.VpnGateway'
)

$cohesionPackages = @(
    'assimalign.cohesion.sdk',
    'assimalign.cohesion.sdk.web',
    'assimalign.cohesion.sdk.database'
) `
    + ($cohesionFrameworks | ForEach-Object { "$($_.ToLowerInvariant()).ref" }) `
    + ($cohesionFrameworks | ForEach-Object { $fw = $_.ToLowerInvariant(); $Rids | ForEach-Object { "$fw.runtime.$_" } })

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
    $tasksDlls = @(
        "assimalign.cohesion.sdk\$cohesionVersion\Tasks\Assimalign.Cohesion.Sdk.Tasks.dll",
        "assimalign.cohesion.sdk.web\$cohesionVersion\Tasks\Assimalign.Cohesion.Sdk.Web.Tasks.dll",
        "assimalign.cohesion.sdk.database\$cohesionVersion\Tasks\Assimalign.Cohesion.Sdk.Database.Tasks.dll"
    )
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
        $cached = Join-Path $globalPackagesRoot $pkg
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
Write-Host "[1/4] Building Cohesion build tasks..." -ForegroundColor Cyan
$buildTasksProj = Join-Path $repoRoot 'build\Tasks\Assimalign.Cohesion.Build.Tasks.csproj'
Write-Host "  build $buildTasksProj" -ForegroundColor DarkGray
& dotnet build $buildTasksProj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $buildTasksProj" }
#endregion

#region 2. SDK packs --------------------------------------------------------
if (-not $SkipSdks) {
    Write-Host "[2/4] Packing SDK projects..." -ForegroundColor Cyan
    # SDK project list. Each entry maps to sdks/<SdkName>/Tasks/<SdkName>.Tasks.csproj.
    # Resource-domain SDKs are scaffolded by New-CohesionDomainScaffold.ps1; each
    # corresponds to a folder under resources/ and a framework family in
    # frameworks/. The base Sdk must come first because the others chain to it.
    $sdkProjects = @(
        'Assimalign.Cohesion.Sdk',
        'Assimalign.Cohesion.Sdk.Web',
        'Assimalign.Cohesion.Sdk.Database',
        'Assimalign.Cohesion.Sdk.ApiManager',
        'Assimalign.Cohesion.Sdk.ConfigurationStore',
        'Assimalign.Cohesion.Sdk.EmailHub',
        'Assimalign.Cohesion.Sdk.EventHub',
        'Assimalign.Cohesion.Sdk.IdentityHub',
        'Assimalign.Cohesion.Sdk.IoTHub',
        'Assimalign.Cohesion.Sdk.LoadBalancer',
        'Assimalign.Cohesion.Sdk.LogSpace',
        'Assimalign.Cohesion.Sdk.MediaHub',
        'Assimalign.Cohesion.Sdk.MessageHub',
        'Assimalign.Cohesion.Sdk.NatGateway',
        'Assimalign.Cohesion.Sdk.NotificationHub',
        'Assimalign.Cohesion.Sdk.Rezolvr',
        'Assimalign.Cohesion.Sdk.Scheduler',
        'Assimalign.Cohesion.Sdk.SecretStore',
        'Assimalign.Cohesion.Sdk.VpnGateway'
    ) | ForEach-Object { Join-Path $repoRoot "sdks\$_\Tasks\$_.Tasks.csproj" }
    foreach ($proj in $sdkProjects) {
        if (-not (Test-Path -LiteralPath $proj)) {
            Write-Host "  (skip, not found) $proj" -ForegroundColor DarkGray
            continue
        }
        Write-Host "  pack $proj" -ForegroundColor DarkGray
        & dotnet pack $proj -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $proj" }
    }
}
else {
    Write-Host "[2/4] Skipping SDK packs (-SkipSdks)." -ForegroundColor DarkYellow
}
#endregion

#region 3. Framework runtime packs (per framework x per RID) ---------------
if (-not $SkipFramework) {
    Write-Host "[3/4] Packing framework runtime pack(s)..." -ForegroundColor Cyan
    foreach ($framework in $cohesionFrameworks) {
        $runtimeProj = Join-Path $repoRoot "frameworks\$framework.Runtime\src\$framework.Runtime.csproj"
        if (-not (Test-Path -LiteralPath $runtimeProj)) {
            Write-Host "  (skip, not found) $runtimeProj" -ForegroundColor DarkGray
            continue
        }
        foreach ($rid in $Rids) {
            Write-Host "  pack $framework runtime ($rid)" -ForegroundColor DarkGray
            & dotnet pack $runtimeProj -c $Configuration -p:RuntimeIdentifier=$rid --nologo
            if ($LASTEXITCODE -ne 0) { throw "Runtime pack failed for $framework / $rid" }
        }
    }
}
else {
    Write-Host "[3/4] Skipping runtime packs (-SkipFramework)." -ForegroundColor DarkYellow
}
#endregion

#region 4. Framework targeting packs (one per framework) -------------------
if (-not $SkipFramework) {
    Write-Host "[4/4] Packing framework targeting pack(s)..." -ForegroundColor Cyan
    foreach ($framework in $cohesionFrameworks) {
        $refsProj = Join-Path $repoRoot "frameworks\$framework.Refs\src\$framework.Refs.csproj"
        if (-not (Test-Path -LiteralPath $refsProj)) {
            Write-Host "  (skip, not found) $refsProj" -ForegroundColor DarkGray
            continue
        }
        Write-Host "  pack $framework refs" -ForegroundColor DarkGray
        & dotnet pack $refsProj -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "Targeting pack failed for $framework" }
    }
}
else {
    Write-Host "[4/4] Skipping targeting pack (-SkipFramework)." -ForegroundColor DarkYellow
}
#endregion

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "Smoke-test it:" -ForegroundColor DarkGray
Write-Host "  dotnet build examples\FrameworkReferenceSmokeTest\FrameworkReferenceSmokeTest.csproj" -ForegroundColor DarkGray
