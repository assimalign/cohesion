# Build System

Cohesion ships as a family of MSBuild SDKs paired with NuGet-distributed shared frameworks, modeled on `Microsoft.NET.Sdk` + `Microsoft.NETCore.App` / `Microsoft.AspNetCore.App`. Understanding this is essential when touching anything under `sdks/`, `frameworks/`, `installer/scripts/`, `.github/workflows/framework.yml`, or any `*.props` / `*.targets` file in `build/`.

## Centralized MSBuild logic — the most drift-prone area

**Shared build logic belongs in `.props` and `.targets` files**, not duplicated in every csproj. This is structurally implied by the build system but worth stating outright because it gets violated in long sessions.

Concrete rules:
- Before adding a `<PropertyGroup>` or `<ItemGroup>` to a csproj, check whether the same setup exists (or should exist) in:
  - `build/Targets/*.props` / `build/Targets/*.targets` — repo-wide build logic
  - `Directory.Build.props` / `Directory.Build.targets` in the relevant folder — scoped to a subtree
  - `frameworks/Assimalign.Cohesion.App.props` — framework membership manifest
- If two or more sibling csprojs would carry the same block, the block belongs in shared build config. Lift it.
- Per-project `<Version>` overrides are forbidden — `$(CohesionVersion)` in `build/Targets/Build.Version.props` is the single source of truth.
- `TargetFramework`, `LangVersion`, `EnablePreviewFeatures`, `IsAotCompatible`, etc. are centrally set. Don't duplicate them per project unless the project genuinely deviates from the repo default.
- Package versions live in `build/Targets/Build.References.Packages.targets`. Add the version there, then use `CohesionPackageReference` in the consuming csproj.

When in doubt: search for the property name in `build/Targets/` first. If it's already there, extend the central definition; don't override locally.

## The consumer experience

```xml
<Project Sdk="Assimalign.Cohesion.Sdk.Web">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
</Project>
```

Plus a `global.json` pinning every Cohesion SDK in the chain:

```json
{
    "sdk": { "version": "10.0.101" },
    "msbuild-sdks": {
        "Assimalign.Cohesion.Sdk":     "10.0.0",
        "Assimalign.Cohesion.Sdk.Web": "10.0.0"
    }
}
```

No installer required. Consumers get every Cohesion library belonging to the chosen framework(s) automatically through the chain `Sdk.<Domain>` → `Sdk` (base) → `Microsoft.NET.Sdk`.

## Each SDK auto-includes one or more `<FrameworkReference>`s

| Consumer SDK | Auto-included frameworks |
| --- | --- |
| `Assimalign.Cohesion.Sdk` | `App` |
| `Assimalign.Cohesion.Sdk.Web` | `App` + `App.Web` |
| `Assimalign.Cohesion.Sdk.Database` | `App` + `App.Database` |
| `Assimalign.Cohesion.Sdk.<Domain>` | `App` + `App.<Domain>` |

The base SDK declares `KnownFrameworkReference` entries for every framework in `sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props`. Chained SDKs only add the additional auto-`<FrameworkReference>` on top of the base.

## Each framework is two NuGet packages

For framework `Assimalign.Cohesion.App.<Domain>`:

- **`Assimalign.Cohesion.App.<Domain>.Ref`** — targeting pack (compile-time reference assemblies). Contains `ref/<tfm>/*.dll` plus `data/FrameworkList.xml`.
- **`Assimalign.Cohesion.App.<Domain>.Runtime.<rid>`** — per-RID runtime pack (implementation assemblies). Contains `runtimes/<rid>/lib/<tfm>/*.dll` plus `data/RuntimeList.xml`. One package per RID; the supported set is declared on the `KnownFrameworkReference`'s `RuntimePackRuntimeIdentifiers`.

The .NET SDK's `ProcessFrameworkReferences` machinery resolves these at restore time and auto-restores from configured NuGet feeds when not already extracted.

## Single source of truth for framework contents

`frameworks/Assimalign.Cohesion.App.props` lists every assembly that ships in every framework. ItemGroups are conditioned on `$(CohesionFrameworkName)` so each framework's Refs/Runtime project sees only its own assemblies:

```xml
<ItemGroup Condition="'$(CohesionFrameworkName)' == 'Assimalign.Cohesion.App.Web'">
    <CohesionFrameworkAssembly Include="Assimalign.Cohesion.App.Web" />
    <CohesionFrameworkAssembly Include="Assimalign.Cohesion.Http" />
    <!-- ...etc... -->
</ItemGroup>
```

Property-based conditions are used (not `%(Framework)` metadata) because MSBuild forbids item-metadata references in top-level `ItemGroup` conditions (MSB4190).

## Adding a library to a framework

A one-line edit to `App.props`:

```xml
<CohesionFrameworkAssembly Include="Assimalign.Cohesion.Scheduler.Jobs" />
```

The Runtime csproj converts the list to `<CohesionProjectReference>` items, which `build/Targets/Build.References.Projects.targets` resolves to matching csprojs under `libraries/**` or `resources/**`. CopyLocal puts the library's DLL into the Runtime project's bin, and `App.targets` packs it into the framework's NuGet packs along with matching entries in `FrameworkList.xml` and `RuntimeList.xml`. Validation in `App.targets` hard-fails if a listed assembly isn't on disk after the build, so a typo or missing project surfaces loudly.

## Adding a new framework + SDK domain

```powershell
# 1. Create the resources/<Name>/ folder if it doesn't exist.

# 2. Generate the SDK + Framework scaffold (7 files):
pwsh installer/scripts/New-CohesionDomainScaffold.ps1 -Name <Name>

# 3. Wire it up (currently manual; mirrors existing entries):
#    a. Add a KnownFrameworkReference block to
#       sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props
#    b. Add a property-conditioned ItemGroup to
#       frameworks/Assimalign.Cohesion.App.props
#    c. Add the framework name to $cohesionFrameworks and the SDK name to
#       $cohesionSdks in installer/scripts/Install-Local.ps1
#    d. Add the new Refs + Runtime folder/project entries to
#       frameworks/Assimalign.Cohesion.Frameworks.slnx

# 4. Verify locally:
pwsh installer/scripts/Install-Local.ps1
```

The scaffold script is idempotent: re-running skips anything already on disk.

## Versioning

`$(CohesionVersion)` lives in `build/Targets/Build.Version.props` and is the single source of truth. Every Cohesion package — SDK, Ref pack, Runtime pack, library — shares this version. Bumping is a one-line edit.

`frameworks/Directory.Build.props` sets `<VersionPrefix>$(CohesionVersion)</VersionPrefix>` so Microsoft.NET.Sdk's default `VersionPrefix=1.0.0` doesn't win. **Don't remove that line** — it's the only thing keeping framework `.nupkg` versions aligned with the SDK.

## Dev loop: `Install-Local.ps1`

Packs all SDKs + framework families (Ref + per-RID Runtime each) into the in-tree feed at `_out/packages/`. The repo's `nuget.config` maps `Assimalign.Cohesion.*` to that feed, so any consumer csproj under the repo (or under a sibling repo whose `nuget.config` points back) restores from the local build.

Flags worth knowing:
- `-Configuration Release` — Release pack (default Debug)
- `-Rids 'win-x64','linux-x64',...` — cross-RID runtime packs (default: host RID)
- `-SkipSdks` / `-SkipFramework` — iterate one half without the other
- `-Force` — bypass the locked-DLL check (see "Recovery from a wedged dev loop" below)

The script's first step is a guard: it tries to open every cached `Assimalign.Cohesion.Sdk[.Family].Tasks.dll` under `~/.nuget/packages/` for exclusive write. If anything (most commonly Visual Studio) has the DLL loaded, it refuses to proceed rather than producing a half-replaced cache. Close VS, then re-run.

## Recovery from a wedged dev loop

If `Install-Local.ps1` aborts with "Cached Cohesion SDK Tasks DLL is file-locked":

1. Close Visual Studio fully (`Get-Process devenv` should return nothing).
2. Optional: kill any leftover dotnet host that still has the DLL loaded (the script tells you the PID).
3. Re-run `Install-Local.ps1`.

If a consumer build complains about an `Assimalign.Cohesion.Sdk` it can't resolve, the usual cause is a missing pin in `global.json`'s `msbuild-sdks` block. `<Import Sdk="X">` chains do not honor inline-version syntax — the consumer's `global.json` must pin every SDK that appears anywhere in the chain.

## CI pipeline summary

`.github/workflows/framework.yml` runs three stages:
1. **Pack** (Linux) — runs `Install-Local.ps1` with all declared RIDs, uploads `.nupkg`s as the `cohesion-packages` artifact.
2. **Smoke-test** (ubuntu/windows/macos matrix) — materializes inline consumer csprojs, builds against targeting packs, publishes self-contained against per-RID runtime packs.
3. **Publish** (`needs: [pack, smoke-test]`, only on `main`) — pushes every `.nupkg` to GitHub Packages.

GitHub Packages is treated as a QA/UAT staging registry: each push to `main` on the same `$(CohesionVersion)` deletes and replaces the previous publish, so `--skip-duplicate` is deliberately omitted (a failed replacement turns CI red instead of silently leaving the old version on the feed).

## File layout reference

```
frameworks/
├── Assimalign.Cohesion.App.props          ← framework membership manifest
├── Assimalign.Cohesion.App.targets        ← collection + manifest writer logic
├── Directory.Build.props                  ← sets VersionPrefix for framework projects
├── Assimalign.Cohesion.App[.Domain].Refs/
│   └── src/...Refs.csproj                 ← produces the .Ref targeting pack
└── Assimalign.Cohesion.App[.Domain].Runtime/
    └── src/...Runtime.csproj              ← produces the .Runtime.<rid> runtime pack(s)

sdks/
└── Assimalign.Cohesion.Sdk[.Domain]/
    ├── Sdk/Sdk.props                      ← what consumers see first
    ├── Sdk/Sdk.targets
    ├── Targets/Sdk.<Domain>.props         ← chained SDKs: per-domain build hooks
    ├── Targets/Sdk.<Domain>.targets
    └── Tasks/...Tasks.csproj              ← code-generation task DLL

sdks/Assimalign.Cohesion.Sdk/Targets/      ← base SDK only
├── ...Sdk.FrameworkReference.props        ← KnownFrameworkReference list (every framework)
├── ...Sdk.Common.props / .targets         ← shared consumer build logic
├── ...Sdk.NameOnly.ProjectReference.targets
├── ...Sdk.StronglyTypedSettings.props / .targets
└── ...Sdk.ApplicationModel.Build.targets

installer/scripts/
├── Install-Local.ps1                      ← dev loop: pack everything locally
├── Get-CohesionVersion.ps1                ← resolves $(CohesionVersion) for scripts + CI
├── New-CohesionDomainScaffold.ps1         ← scaffold a new SDK + Framework pair
└── Cleanup-PriorRegistrations.ps1         ← one-shot cleanup for old MSI-based registrations

.github/scripts/
└── Publish-Nupkg.ps1                      ← delete-then-push helper for GitHub Packages
```

## Architecture rules (hard constraints)

1. **Never hardcode a version in a chained `<Import Sdk>` element.** That syntax (`Sdk="X/version"`) is not honored on `<Import Sdk>`; only on `<Project Sdk>`. The consumer's `global.json` pins the version.
2. **Never put a framework's full content under one csproj.** Each Cohesion library is its own project under `libraries/` or `resources/`; the framework's Runtime project is purely a packaging shell whose `<CohesionProjectReference>` items come from the declarative `App.props`.
3. **Never bypass the `$(CohesionVersion)` chain.** No per-project `<Version>` overrides. If a project needs a different version, that's a sign it should ship outside the framework, not inside it.
4. **`FrameworkList.xml` and `RuntimeList.xml` are build artifacts.** They're in `.gitignore`. The collection target in `App.targets` regenerates them on every pack; never edit them by hand.
5. **The base `Sdk` registers every framework's `KnownFrameworkReference`.** Adding the registration in a chained SDK (`Sdk.Web`, etc.) doesn't propagate to consumers using only the base SDK; everything goes through the base.
