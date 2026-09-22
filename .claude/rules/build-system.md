---
paths:
  - "**/*.csproj"
  - "**/*.props"
  - "**/*.targets"
  - "**/*.slnx"
  - "global.json"
  - "build/**"
  - "frameworks/**"
  - "sdks/**"
  - "installer/**"
  - ".github/workflows/**"
---

# Build System

Cohesion ships as a family of MSBuild SDKs paired with NuGet-distributed shared frameworks, modeled on `Microsoft.NET.Sdk` + `Microsoft.NETCore.App` / `Microsoft.AspNetCore.App`. Understanding this is essential when touching anything under `sdks/`, `frameworks/`, `installer/scripts/`, `.github/workflows/sdk-smoke.yml`, `.github/workflows/release.yml`, or any `*.props` / `*.targets` file in `build/`.

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

When editing project files, prefer Cohesion-specific MSBuild items over stock items wherever one exists — not just the ones enumerated here.

### Shipped props and targets must evaluate under Visual Studio's MSBuild

Visual Studio evaluates projects with the **.NET Framework** MSBuild inside `devenv`, while every
command-line loop here (`dotnet build`, `dotnet test`, the SDK package-boundary tests) uses Core
MSBuild. A property function that exists only on .NET Core evaluates cleanly on the command line
and then fails **every** consumer's project load in the IDE with MSB4186 "Invalid static method
invocation syntax". The one that bit twice is the two-argument `Path.GetFullPath`:

```xml
<!-- Wrong: .NET Framework has no GetFullPath(path, basePath) overload. -->
<_Full>$([System.IO.Path]::GetFullPath('$(Relative)', '$(MSBuildProjectDirectory)'))</_Full>
<!-- Right: MSBuild's own helper, identical semantics on both runtimes. -->
<_Full>$([MSBuild]::NormalizePath('$(MSBuildProjectDirectory)', '$(Relative)'))</_Full>
```

Use `[MSBuild]::NormalizePath` / `NormalizeDirectory` for relative-to-absolute path math in any
shipped `.props` or `.targets`. `sdks/Assimalign.Cohesion.Sdk/Tasks/tests/VisualStudioMsBuildCompatibilityTests.cs`
scans `sdks/**` for the two-argument form; before declaring MSBuild work done, also evaluate one
consumer with Visual Studio's MSBuild (`vswhere -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe`,
then `MSBuild.exe <consumer>.csproj -t:Restore`), because nothing else in the loop runs it.

## The consumer experience

```xml
<Project Sdk="Assimalign.Cohesion.Sdk.Web">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
</Project>
```

Plus a `global.json` pinning every Cohesion SDK in the chain:

```json
{
    "sdk": {
        "version": "10.0.300",
        "rollForward": "latestFeature"
    },
    "msbuild-sdks": {
        "Assimalign.Cohesion.Sdk":     "10.0.0",
        "Assimalign.Cohesion.Sdk.Web": "10.0.0"
    }
}
```

The SDK pin check in `sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.PinValidation.targets` requires .NET SDK >= 10.0.300 (`COHSDK002`); the repository's `global.json` is the canonical example.

No installer required. Resource consumers get the hosting kernel and their area framework through the chain `Sdk.<Domain>` → `Sdk` (base) → `Microsoft.NET.Sdk`. Direct base-SDK consumers get build tooling only and choose packages/frameworks explicitly.

## Each SDK auto-includes one or more `<FrameworkReference>`s

| Consumer SDK | Auto-included frameworks |
| --- | --- |
| `Assimalign.Cohesion.Sdk` | none |
| `Assimalign.Cohesion.Sdk.Web` | `App` + `App.Web` |
| `Assimalign.Cohesion.Sdk.Database` | `App` + `App.Database` |
| `Assimalign.Cohesion.Sdk.<Domain>` | `App` + `App.<Domain>` |

The base SDK declares `KnownFrameworkReference` entries for every framework in `sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props`, so explicit references resolve even in a base-only project. Each resource-area SDK sets its executable marker before importing the base props, then adds both implicit references under `CohesionAutoIncludeAppFramework != false`. Gateway remains NuGet-only unless in-process composition adds App plus the referenced areas.

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

## Adding a library to an area framework

Add the library to its `App.<Area>` group in `App.props`:

```xml
<CohesionFrameworkAssembly Include="Assimalign.Cohesion.Scheduler.Jobs" />
```

The Runtime csproj converts the list to `<CohesionProjectReference>` items, which `build/Targets/Build.References.Projects.targets` resolves to matching csprojs under `libraries/**` or `resources/**`. CopyLocal puts the library's DLL into the Runtime project's bin, and `App.targets` packs it into the framework's NuGet packs along with matching entries in `FrameworkList.xml` and `RuntimeList.xml`. Validation in `App.targets` hard-fails if a listed assembly isn't on disk after the build, so a typo or missing project surfaces loudly. The framework tests compute every area's shipped closure and reject a missing public/private entry.

Base App is different: never add a library directly to its assembly list. `@(CohesionAppKernelRoot)` in `App.props` is the sole policy input, and `App.targets` derives the transitive Assimalign project-reference closure plus the umbrella assembly. The roots are Hosting and its Health/Resources/Telemetry siblings, Connections, the host-composed configuration providers, Logging.Console, OpenTelemetry, DependencyInjection, and FileSystem.Physical. Connections is a kernel root because generated resource accessors and every area hosting module depend on it. Everything outside that hosting kernel remains an ordinary package.

The Hosting-area package graph is exact: `Assimalign.Cohesion.Hosting.Health` references Core only;
`Assimalign.Cohesion.Hosting.Resources` references Core, plain Hosting, Hosting.Health, and the
ProtectedData facade; plain Hosting references neither sibling. The `Assimalign.Cohesion.App`
hosting kernel carries both opt-in siblings. All 18 resource SDKs emit generated code that references
`Assimalign.Cohesion.Hosting.Resources.ResourceRuntime`, so Resources belongs beside the
already-shared plain Hosting assembly; Health follows because Resources references it. This
framework-level delivery does not add direct project references to the 16 filler resource areas.
Those generated accessors also expose `Assimalign.Cohesion.Connections.ConnectionString`, and
every area hosting module reaches Connections, so Connections is carried once by App rather than
repeated in all 18 area frameworks.

## Cross-resource dependencies (private implementation details)

A library sometimes needs another library as an internal implementation detail without exposing that dependency to its consumers (canonical example: `Assimalign.Cohesion.Database` uses `Assimalign.Cohesion.Web` for HTTP transport, but `Sdk.Database` consumers should see database types only). Two coordinated items make this work:

- **`CohesionPrivateProjectReference`** (in the library csproj) — resolves by name like `CohesionProjectReference`, but emits `PrivateAssets="all"`: compiles in and CopyLocals, yet never appears as a `<dependency>` in the library's `.nupkg`.
- **`CohesionFrameworkPrivateAssembly`** (in `frameworks/Assimalign.Cohesion.App.props`) — the framework's Runtime pack ships the DLL (listed in `RuntimeList.xml`), but the Ref pack omits it, so consumers never see the types in IntelliSense while the host resolves them at run time.

```xml
<!-- library csproj -->
<CohesionPrivateProjectReference Include="Assimalign.Cohesion.Web" />
<!-- frameworks/Assimalign.Cohesion.App.props, in the owning framework's ItemGroup -->
<CohesionFrameworkPrivateAssembly Include="Assimalign.Cohesion.Web" />
```

Privacy is enforced at the package boundary, not the type system — keep cross-library uses of the private dep `internal`, and expose proxy types publicly if hosts need to configure the underlying piece. Forgetting the `Private` variants either leaks the dep into the `.nupkg` (used `CohesionProjectReference`) or crashes the host at run time (missing `CohesionFrameworkPrivateAssembly`). A leak surfaces downstream as a `CS0012` for the consumer, because the private assembly isn't in their reference graph.

Also available: `CohesionCodeGenValueType` for generating strongly typed value objects.

## Resource orchestration dependency guards

`build/Targets/Build.Rules.targets` protects the boundary between resource-area packages and the
orchestration gateway:

- **COHAM001** is the strict dependency-closure guard for an assembly under `resources/**` whose
  name ends in `.ApplicationModel`. It activates only when that project sets
  `<CohesionApplicationModelGuard>true</CohesionApplicationModelGuard>`; every resource
  `*.ApplicationModel` project — all 18 at HEAD — sets the guard. Once active, the only permitted non-BCL assemblies
  are `Assimalign.Cohesion.Core` (the evaluated Core assembly name),
  `Assimalign.Cohesion.ApplicationModel`, `Assimalign.Cohesion.Hosting`,
  `Assimalign.Cohesion.Hosting.Health`, and `Assimalign.Cohesion.Hosting.Resources`. The area's
  ApplicationModel project directly references only `Assimalign.Cohesion.ApplicationModel` and
  `Assimalign.Cohesion.Hosting.Resources`; the latter brings the plain host and health contracts
  into the resolved closure. BCL means assemblies supplied by the `Microsoft.NETCore.App`
  reference pack, plus the `System.Security.Cryptography.ProtectedData` facade used by
  Hosting.Resources' Windows mount reader; third-party packages are not implicitly allowed.
- **COHRES003** applies automatically to every non-harness project under `resources/**` and bans
  any `Assimalign.Cohesion.ApplicationModel.Gateway*` assembly. It has no opt-in and no exemption.
- **COHRES004** rejects `Assimalign.Cohesion.Hosting` and every `Assimalign.Cohesion.Hosting.*`
  assembly for roots and features. Only `<Area>.Hosting`, `<Area>.Hosting.<Suffix>`,
  `<Area>.Testing`, and `<Area>.ApplicationModel` may depend on that closure. The hosting
  family is classified case-insensitively.
  COHRES001 separately rejects an area's exact runtime module and rejects hosting-family
  integrations from roots/features. Each assembly is filtered against the project's named
  exemptions independently. COHRES002 still checks only the exact runtime module and excludes the module's own hosting family.

All three guards inspect the direct/transitive project-reference graph before assembly resolution and
the complete `ReferencePath` closure after `ResolveAssemblyReferences`. The latter catches package
assets and raw `<Reference>`+`HintPath` routes. Tests, examples, and samples are exempt; error text
names each offending assembly so the dependency can be removed at its source.

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
#    d. Add the same two names to $script:CohesionReleaseFramework and
#       $script:CohesionReleaseSdk in
#       installer/scripts/modules/CohesionPackaging.psm1 -- otherwise the new
#       family builds locally but never ships
#    e. Add the new Refs + Runtime folder/project entries to
#       frameworks/Assimalign.Cohesion.Frameworks.slnx

# 4. Verify locally:
pwsh installer/scripts/Install-Local.ps1
```

The scaffold script is idempotent: re-running skips anything already on disk.

Step 3d is guarded, not merely documented: `Assert-CohesionReleaseInventory` fails if a `sdks/<name>` or `frameworks/<family>.{Refs,Runtime}` project exists on disk but is missing from the release lists, so forgetting it turns the next release red rather than shipping a family short.

## Versioning

`$(CohesionVersion)` lives in `build/Targets/Build.Version.props` and is the single source of truth. Every Cohesion package — SDK, Ref pack, Runtime pack, library — shares this version. Bumping is a one-line edit.

The current version line is `<CohesionPatchVersion>0-preview.1</CohesionPatchVersion>`, which resolves to `10.0.0-preview.1`. Rule of record: **cohesion's version never sorts below any version present on a feed.** Inspect GitHub Packages and nuget.org before selecting a line, and bump `main` immediately after tagging so development never moves behind a published version.

Local packages are distinct: `Install-Local.ps1` appends `.local` to the canonical prerelease (`10.0.0-preview.1.local`) and refuses a stable canonical line until its post-tag bump lands. Release packages reject the reserved `local` identifier. The complete staging, promotion, post-tag, and rate-limit policy is in [`docs/VERSIONING_RELEASE_POLICY.md`](../../docs/VERSIONING_RELEASE_POLICY.md).

`frameworks/Directory.Build.props` sets `VersionPrefix` and `VersionSuffix` from `CohesionVersionPrefix` and `CohesionVersionSuffix` so Microsoft.NET.Sdk's default `VersionPrefix=1.0.0` doesn't win. **Don't remove that mapping** — it is what keeps framework `.nupkg` versions aligned with the SDK without feeding a prerelease suffix to `AssemblyVersion`.

## Dev loop: `Install-Local.ps1`

Packs all SDKs + framework families (Ref + per-RID Runtime each) into the in-tree feed at `_out/packages/`, using the canonical prerelease plus `.local`. Before rebuilding, it removes only that local-version cache entry for each selected SDK/framework/library package and prunes stale library/resource package files from the flat feed by exact package id. Consumers restore from that feed via a `nuget.config` mapping `Assimalign.Cohesion.*` to it — the repo does not currently check one in, so add the mapping in the consumer (or a local repo-root `nuget.config`) when smoke-testing.

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

Per-area CI and the publish-less SDK consumer smoke prove the branch; **the release pipeline alone ships it.** Nothing else publishes.

### Per-area CI — `library-*.yml`, `resource-*.yml`

Path-filtered on push, each a thin matrix over project names calling the shared composite action at `.github/actions/build/action.yml` across ubuntu/windows/macos. The action restores, builds, and tests. **It does not pack or push** — publishing from there had every area workflow racing to push a package built from whatever was on `main`, at a version that had passed no release gate. These workflows declare `permissions: contents: read` only.

### Release — `.github/workflows/release.yml`

There are two entry paths. A **published GitHub Release** whose tag is `v$(CohesionVersion)`, prerelease suffix included, always validates, packs, and stages. Public promotion is a separate `workflow_dispatch` from the default branch that names the already-published tag and explicitly sets the Boolean `promote` input to `true`; its default is `false`. Both paths run the same six jobs:

1. **prepare** — resolves the tag to a commit, proves it is reachable from `main`, validates the version against `Get-CohesionVersion.ps1`, and emits the validation matrix from `Get-ReleaseMatrix.ps1`. Fails fast, before the large build matrix runs. Every downstream job checks out **that commit**, not the tag, so a tag moved mid-run cannot publish something no job built.
2. **validate-release** — one leg per shipping package (Linux only; the per-area workflows already carry the three-OS matrix), running the same `.github/actions/build` recipe at the release commit.
3. **pack-packages** — runs `Pack-Release.ps1`, asserts the inventory-derived package set and its metadata, writes the exact release count to `package-order.txt`, and uploads `_out/release/packages` as the `Assimalign.Cohesion.Packages` artifact.
4. **validate-consumer** — downloads that exact artifact and, on Ubuntu, Windows, and macOS, builds package-only SDK consumers, publishes them self-contained for the host RID, runs them, and asserts framework-analyzer generated output. It never repacks or publishes.
5. **publish-github-packages** — stages the consumer-validated artifact in GitHub Packages with `--skip-duplicate`. This is unconditional after a matching published release validates; the manual promotion path safely restages the same immutable version as a no-op.
6. **publish-nuget** — runs only on `workflow_dispatch` with `promote=true`, promotes the same artifact to nuget.org via OIDC (`NuGet/login`), and is routed through the `nuget-org` environment. That environment must be created with required reviewers; referencing its name does not configure protection. Only `-preview.` and `-rc.` versions are eligible; alpha/beta and stable versions stop at staging.

Both publish jobs re-verify `checksums.sha256` before pushing, so "what we published is what we validated" is checked, not assumed.

**The release inventory is the contract.** `installer/scripts/modules/CohesionPackaging.psm1` is the single source of what ships: the curated library/resource list, the SDK families, the framework families, and the RIDs. The release validation matrix, the pack plan, and `Install-Local.ps1`'s local feed all read it, so none of them can disagree about what exists.

A package ships only if **(1)** a per-area CI workflow builds it, **(2)** it is not `IsPackable=false`, and **(3)** it has at least one source file. `Assert-CohesionReleaseInventory` enforces all three in both directions — a package CI never built cannot ship, and a packable project CI does build cannot be silently omitted — plus three guards that the build itself cannot provide:

- **Dependency closure.** A public `CohesionProjectReference` becomes a `<dependency>` in the `.nuspec`. If the target is not itself shipped, the package publishes green and then restores to NU1101 for every consumer — permanently, since nuget.org unlists but never deletes. A name that resolves to no project at all is dropped silently by the reference resolver, so that case warns rather than fails.
- **No empty packages.** Seven projects under `libraries/` and `resources/` currently compile to an empty assembly. They stay in CI and still reach consumers inside the framework packs, but the release publishes no standalone package for them; `$script:CohesionReleaseSourcelessPackage` is the deliberate opt-in for reserving such an id anyway.
- **No matrix blind spots.** Every packable project with source under `libraries/`, `resources/`, `sdks/`, `frameworks/`, `analyzers/`, `tooling/`, or `extensions/` must appear in a workflow's static `projects` matrix or have an exact-path entry with a non-empty reason in `$script:CohesionCiMatrixExclusion`. This catches a project omitted from both the inventory and CI, which the two-way set comparison cannot see. Existing gaps are recorded individually rather than hidden by path or name wildcards.

Note what (1) does *not* claim: 12 shipping entries have no tests csproj beside them, so "CI builds it" is the guarantee and "CI tests it" is true of most, not all.

Well over a third of the `src` csprojs under `libraries/` and `resources/` are scaffolded placeholders. That is why the inventory is curated rather than globbed, and why `Pack-Release.ps1` has no analog of `Install-Local.ps1`'s `-ContinueOnLibraryError`: a project that does not build does not ship.

`.github/workflows/release-inventory.yml` runs the same guard on every push and pull request that touches a workflow, an installer script, the repository `Directory.Build.props`, or anything under a scanned project root, so a new project or its first source file is checked by the change that creates the drift rather than by a later release. After the pack, `Assert-CohesionPackageMetadata` opens every produced archive and fails unless the central NuGet icon actually landed — the icon is wired through an MSBuild import chain, and a project that falls out of that chain packs cleanly and silently unbranded.

Only per-area release-library matrices participate in the inventory equality check. The exact non-matrix workflow set is `release.yml`, `release-inventory.yml`, `analyzers.yml`, `sdk-smoke.yml`, and `credential-guard.yml`. Static project matrices in any workflow, including non-matrix workflows such as `analyzers.yml` and `sdk-smoke.yml`, still count for the repository-wide blind-spot check.

Every external `uses:` in `release.yml`, `sdk-smoke.yml`, and `.github/actions/build/action.yml` is pinned to a commit SHA with a **trailing** version comment, because a mutable tag could run attacker-controlled code with a workflow's package or nuget.org OIDC identity. The comment must be trailing: that is the form Dependabot rewrites when it bumps a SHA, and `.github/dependabot.yml` carries an entry for both directories.

### SDK consumer smoke — `.github/workflows/sdk-smoke.yml`

On pull requests and relevant pushes, a three-OS matrix runs strict `Pack-Release.ps1 -SkipLibraries` for the host RID and packs the Core/ObjectMapping packages used by the analyzer sample. The shared `.github/scripts/Invoke-SdkConsumerSmoke.ps1` harness then materializes isolated base, Web, Database, and analyzer-bearing consumers against that feed. The base executables explicitly reference App; the analyzer references ObjectMapping as an ordinary package. The harness builds, asserts generated source, publishes self-contained, runs each apphost, and asserts its output. The same job runs the framework manifest closure tests.

The workflow retains the broader `Sdk.Gateway` package-boundary, in-process, NativeAOT, and container smoke added with that SDK. Its permissions remain `contents: read`; artifact uploads are run-local test inputs, never package-feed publication.

`release.yml` repeats the same package-only consumer harness as `validate-consumer`, using the exact full release artifact, before staging or promotion. The former publishing overlap is resolved: GitHub Packages and nuget.org have one writer, `release.yml`, and release versions use immutable `--skip-duplicate` semantics.

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
    └── Tasks/                             ← the task project, laid out like every other project
        ├── src/...Tasks.csproj            ← code-generation task DLL
        ├── tests/                         ← its tests, where the family has any
        └── docs/                          ← its OVERVIEW.md / DESIGN.md, where the family has any

sdks/Assimalign.Cohesion.Sdk/Targets/      ← base SDK only
├── ...Sdk.FrameworkReference.props        ← KnownFrameworkReference list (every framework)
├── ...Sdk.Common.props                    ← shared consumer build logic
├── ...Sdk.NameOnly.ProjectReference.targets
├── ...Sdk.StronglyTypedSettings.props / .targets
├── ...Sdk.Defaults.props                  ← SDK-owned consumer project defaults
└── ...Sdk.PinValidation.targets           ← SDK and platform version-pin validation

sdks/Assimalign.Cohesion.Sdk.ApplicationModel/Targets/
├── ...Sdk.ResourceManifest.props          ← resource manifest metadata defaults
├── RESOURCE_MANIFEST_README.md            ← resource manifest build contract
├── Sdk.Image.targets                     ← OCI image production and publication gather
├── Sdk.Resource.props                    ← resource opt-in properties
├── Sdk.Resource.Paths.targets            ← late RID/TFM intermediate-path defaults
├── Sdk.Resource.targets                  ← manifest and resource surface generation
└── ...Sdk.ApplicationModel.Build.targets

installer/scripts/
├── modules/
│   └── CohesionPackaging.psm1             ← THE release inventory + its drift guards
├── Pack-Release.ps1                       ← strict full or -SkipLibraries pack
├── Get-ReleaseMatrix.ps1                  ← release validation matrix (JSON) for release.yml
├── Install-Local.ps1                      ← dev loop: pack everything locally
├── Get-CohesionVersion.ps1                ← resolves $(CohesionVersion) for scripts + CI
├── New-CohesionDomainScaffold.ps1         ← scaffold a new SDK + Framework pair
└── Cleanup-PriorRegistrations.ps1         ← one-shot cleanup for old MSI-based registrations

.github/scripts/
└── Invoke-SdkConsumerSmoke.ps1            ← package-only build/publish/run harness

.github/workflows/
├── sdk-smoke.yml                          ← publish-less three-OS SDK validation
└── release.yml                            ← sole package publisher + consumer gate

build/Targets/
├── Build.Branding.props                   ← package metadata + <PackageIcon>
└── Build.Packaging.targets                ← packs the icon into every packable project

assets/branding/nuget/
└── cohesion-nuget-mono-light-128.png      ← imported from the branding repo; see the README
```

## The SDK family layout

An SDK family is `sdks/Assimalign.Cohesion.Sdk[.Domain]/` holding `Sdk/`, `Targets/`, and `Tasks/`.
`Sdk/` and `Targets/` are **shipped content** — loose props/targets copied verbatim into the nupkg at
those same paths. `Tasks/` is an ordinary project folder: `Tasks/src/` holds the task csproj and its
sources, `Tasks/tests/` its tests, `Tasks/docs/` its documentation. That makes every SDK match the
`src/` + `tests/` + `docs/` shape used everywhere else in the repo.

The package layout is **not** the repo layout. Inside the nupkg, `Tasks/` contains the built
`*.Tasks.dll`, which is why `Targets/*.targets` reach it as `..\Tasks\<Name>.Tasks.dll` from
`Targets/`. Those `AssemblyFile` paths describe the package and must not be rewritten to follow a
repo-side folder move.

`sdks/Directory.Build.props` derives `$(CohesionSdkRootDirectory)` from the project's own location,
and `sdks/Directory.Build.targets` packs `Sdk/` and `Targets/` relative to **that**, scoped to the
packable Tasks project. Do not reintroduce `$(MSBuildProjectDirectory)\..`: it silently produced an
SDK package with no `Sdk/` or `Targets/` folder the moment a project changed depth, and a consumer
only discovers that at SDK-resolution time. Verify a layout change by packing and listing the
archive — `Sdk/`, `Targets/`, and `Tasks/*.dll` must all be present.

Adding a family through `New-CohesionDomainScaffold.ps1` emits this layout already; the release
inventory in `CohesionPackaging.psm1` and `Install-Local.ps1` both resolve
`sdks/<name>/Tasks/src/<name>.Tasks.csproj`.

## Architecture rules (hard constraints)

1. **Never hardcode a version in a chained `<Import Sdk>` element.** That syntax (`Sdk="X/version"`) is not honored on `<Import Sdk>`; only on `<Project Sdk>`. The consumer's `global.json` pins the version.
2. **Never put a framework's full content under one csproj.** Each Cohesion library is its own project under `libraries/` or `resources/`; the framework's Runtime project is purely a packaging shell whose `<CohesionProjectReference>` items come from the declarative `App.props`.
3. **Never bypass the `$(CohesionVersion)` chain.** No per-project `<Version>` overrides. If a project needs a different version, that's a sign it should ship outside the framework, not inside it.
4. **`FrameworkList.xml` and `RuntimeList.xml` are build artifacts.** They're in `.gitignore`. The collection target in `App.targets` regenerates them on every pack; never edit them by hand.
5. **The base `Sdk` registers every framework's `KnownFrameworkReference`.** Adding the registration in a chained SDK (`Sdk.Web`, etc.) doesn't propagate to consumers using only the base SDK; everything goes through the base.
