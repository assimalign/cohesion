# Cohesion Coding Rules

This file is the canonical instruction source for AI agents working in this repository. The GitHub Copilot companion file at `.github/copilot-instructions.md` should mirror this document rather than introduce competing rules.

This document defines specific coding standards and rules for the Cohesion project. These rules are enforced by AI agents and code reviews.

## Repository Context

### Development Environment

- .NET SDK `10.0.300` or later is required and pinned in `global.json`
- Projects compile with `LangVersion=Preview` and `EnablePreviewFeatures=true`
- Libraries target `net10.0` through the shared build configuration
- NativeAOT compatibility is a standing requirement across the repo

### Quick Commands

```powershell
# Build the repository
dotnet build

# Build a specific project
dotnet build libraries/Core/Assimalign.Cohesion.Core/src/Assimalign.Cohesion.Core.csproj

# Run a project's tests
dotnet test libraries/Core/Assimalign.Cohesion.Core/tests/

# Create packages
dotnet pack --configuration Release

# Clean outputs
dotnet clean

# Pack every Cohesion SDK + Framework family into _out/packages/
# (the dev loop for consumer-side smoke testing; see "Framework + SDK
# Architecture" below for what this actually produces)
pwsh installer/scripts/Install-Local.ps1
```

### Output Directories
- `_out/packages/` for packaged library outputs
- `_out/dotnet/sdk/` for SDK and build output

### Repository Structure

- `libraries/` contains shared libraries, infrastructure, runtime, and cross-service foundations
- `resources/` contains service and resource implementations. Every folder under `resources/` has a corresponding `Sdk.<Name>` and `App.<Name>` framework family - see "Framework + SDK Architecture" below
- `frameworks/` contains the shared-framework producer projects (one Refs project + one Runtime project per framework family) plus the authoritative manifest files `Assimalign.Cohesion.App.props` and `Assimalign.Cohesion.App.targets`
- `build/` contains custom MSBuild logic, centralized targets, and package-version management. `build/Targets/Build.Version.props` is the single source of truth for `$(CohesionVersion)`
- `sdks/` contains Cohesion SDK projects. Each SDK family (`Sdk`, `Sdk.Web`, `Sdk.<Domain>`) is a separate folder; `Sdk` is the base and the others chain to it
- `installer/` contains the WiX MSI source plus development scripts (`Install-Local.ps1`, `Publish-Nupkg.ps1`, `New-CohesionDomainScaffold.ps1`)
- `extensions/` and `tooling/` contain developer tooling and integration surfaces
- `docs/` contains repository-level documentation

### Build System Context

- Prefer Cohesion-specific MSBuild items over stock items where available
- Internal dependencies should use `CohesionProjectReference`
- Internal dependencies that must NOT flow to consumers of the resulting `.nupkg` should use `CohesionPrivateProjectReference` (paired with a `CohesionFrameworkPrivateAssembly` entry on the owning framework - see "Cross-resource dependencies" below)
- External packages should use `CohesionPackageReference`
- Central package versions are managed in `build/Targets/PackageReferences.targets`
- Strongly typed value objects may be generated through `CohesionCodeGenValueType`

### GitHub Project Execution Metadata

When work is coming from the Cohesion GitHub Project, treat project fields as execution guidance rather than as decorative labels.

- `Priority` expresses urgency and criticality. Lower numbers are higher priority, so `P001` should be considered before `P002`.
- `Wave` expresses planned delivery order. Lower numbers are earlier waves, so `W01` should generally be delivered before `W02` and `W03`.
- When selecting work autonomously, prefer items that are both unblocked and in the earliest available `Priority` and `Wave`.
- Do not pull later-wave work forward ahead of earlier-wave blockers unless the user explicitly asks for it or the dependency graph makes prerequisite work necessary.
- If issue body details, dependency relationships, `Priority`, and `Wave` conflict, resolve them in this order: explicit user instruction, dependency or blocker relationships, `Priority`, then `Wave`.
- Preserve later-wave requirements in planning and design notes even when only implementing current-wave scope.
- When a ticket requires prerequisite work from another ticket, call that out explicitly rather than silently skipping the project ordering.

### Backlog Authoring Guidance

When creating or refining GitHub backlog items, include architectural boundary guidance when it helps a future implementation session make better decisions.

- If a design naturally decomposes into a project family, call out the suggested project family in the feature or story body.
- Suggested project families are advisory implementation guidance unless the ticket explicitly marks them as required.
- When suggesting a project family, include the candidate project names, the responsibility of each project, and the intended dependency direction between them.
- Call out boundaries that matter for AOT, source generation, validation, serialization, transport, or service integration so later implementation does not have to rediscover them from scratch.
- Use backlog issue bodies to preserve this architectural context even when placeholder folders or placeholder projects already exist in the repository.

## Framework + SDK Architecture

Cohesion ships as a family of MSBuild SDKs paired with NuGet-distributed
shared frameworks, modeled on `Microsoft.NET.Sdk` + `Microsoft.NETCore.App` /
`Microsoft.AspNetCore.App`. Understanding this is essential when touching
anything under `sdks/`, `frameworks/`, `installer/scripts/`, or
`.github/workflows/framework.yml`.

### The consumer experience

```xml
<Project Sdk="Assimalign.Cohesion.Sdk.Web">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
    </PropertyGroup>
</Project>
```

Plus a `global.json` pinning every Cohesion SDK the project (or its chain)
references:

```json
{
    "sdk": { "version": "10.0.101" },
    "msbuild-sdks": {
        "Assimalign.Cohesion.Sdk":     "10.0.0",
        "Assimalign.Cohesion.Sdk.Web": "10.0.0"
    }
}
```

That's it. No installer required. The consumer gets every Cohesion library
that belongs to the chosen framework(s) automatically through the chain
`Sdk.<Domain>` → `Sdk` (base) → `Microsoft.NET.Sdk`.

### Each SDK auto-includes one or more `<FrameworkReference>`s

| Consumer SDK | Auto-included frameworks |
| --- | --- |
| `Assimalign.Cohesion.Sdk` | `App` |
| `Assimalign.Cohesion.Sdk.Web` | `App` + `App.Web` |
| `Assimalign.Cohesion.Sdk.Database` | `App` + `App.Database` |
| `Assimalign.Cohesion.Sdk.<Domain>` | `App` + `App.<Domain>` |

The base SDK declares `KnownFrameworkReference` entries for every framework
in `sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props`,
so the registrations are inherited by every chained SDK. The chained SDKs
only add the additional auto-`<FrameworkReference>` on top of the base.

### Each framework is two NuGet packages

For framework `Assimalign.Cohesion.App.<Domain>`:

- **`Assimalign.Cohesion.App.<Domain>.Ref`** is the targeting pack (compile-time
  reference assemblies). Contains `ref/<tfm>/*.dll` plus `data/FrameworkList.xml`.
- **`Assimalign.Cohesion.App.<Domain>.Runtime.<rid>`** is the per-RID runtime pack
  (implementation assemblies). Contains `runtimes/<rid>/lib/<tfm>/*.dll` plus
  `data/RuntimeList.xml`. One package per RID; the supported set is declared
  on the `KnownFrameworkReference`'s `RuntimePackRuntimeIdentifiers`.

The .NET SDK's `ProcessFrameworkReferences` machinery resolves these at
restore time. When the packs are not already extracted under
`$(NetCoreTargetingPackRoot)` / `$(NetCoreRuntimePackRoot)`, the SDK
auto-restores them from the configured NuGet feeds (same path NuGet uses
for any other PackageReference).

### Single source of truth for framework contents

`frameworks/Assimalign.Cohesion.App.props` lists every assembly that ships
in every framework. ItemGroups are conditioned on `$(CohesionFrameworkName)`
so each framework's Refs/Runtime project sees only its own assemblies:

```xml
<ItemGroup Condition="'$(CohesionFrameworkName)' == 'Assimalign.Cohesion.App.Web'">
    <CohesionFrameworkAssembly Include="Assimalign.Cohesion.App.Web" />
    <CohesionFrameworkAssembly Include="Assimalign.Cohesion.Http" />
    <!-- ...etc... -->
</ItemGroup>
```

Property-based conditions are used (not `%(Framework)` metadata) because
MSBuild forbids item-metadata references in top-level `ItemGroup` conditions
(MSB4190).

### Adding a library to a framework

A one-line edit to `App.props`:

```xml
<CohesionFrameworkAssembly Include="Assimalign.Cohesion.Scheduler.Jobs" />
```

The Runtime csproj converts the list to `<CohesionProjectReference>` items,
which `build/Targets/Build.NameOnly.ProjectReferences.targets` resolves to
matching csprojs under `libraries/**` or `resources/**`. CopyLocal puts the
library's DLL into the Runtime project's bin, and `App.targets` packs it
into the framework's NuGet packs along with a matching entry in
`FrameworkList.xml` and `RuntimeList.xml`. The validation in `App.targets`
hard-fails if a listed assembly isn't on disk after the build, so a typo or
missing project surfaces loudly.

### Cross-resource dependencies (private implementation details)

A library sometimes needs another library as an internal implementation
detail without exposing that dependency to its consumers. Canonical
example: the database server piece inside `Assimalign.Cohesion.Database`
uses `Assimalign.Cohesion.Web` for HTTP transport, but a developer writing
`<Project Sdk="Assimalign.Cohesion.Sdk.Database">` should see database
types only - Web is a hidden runtime concern.

Two coordinated items make this work:

- **`CohesionPrivateProjectReference`** (in the library csproj) resolves
  by name like `CohesionProjectReference`, but the emitted
  `<ProjectReference>` carries `PrivateAssets="all"`. The target compiles
  in and CopyLocals to bin as usual, but does NOT appear as a
  `<dependency>` in the library's `.nupkg` `.nuspec`. Direct consumers
  of that `.nupkg` never learn about the private library.

- **`CohesionFrameworkPrivateAssembly`** (in
  `frameworks/Assimalign.Cohesion.App.props`) sits alongside
  `CohesionFrameworkAssembly` in the per-framework ItemGroup. The
  framework's Runtime pack ships its DLL and lists it on `RuntimeList.xml`;
  the Ref pack does NOT include it and `FrameworkList.xml` omits it. Net
  effect: consumers of `Sdk.<Domain>` never see the type in IntelliSense,
  but the host resolves it at run time.

Worked example. In `resources/Database/.../Assimalign.Cohesion.Database.csproj`:

```xml
<CohesionProjectReference        Include="Assimalign.Cohesion.Core" />
<CohesionProjectReference        Include="Assimalign.Cohesion.Database.Execution" />
<CohesionPrivateProjectReference Include="Assimalign.Cohesion.Web" />
```

In `frameworks/Assimalign.Cohesion.App.props`, under the App.Database
ItemGroup:

```xml
<CohesionFrameworkAssembly        Include="Assimalign.Cohesion.App.Database" />
<CohesionFrameworkAssembly        Include="Assimalign.Cohesion.Database" />
<CohesionFrameworkPrivateAssembly Include="Assimalign.Cohesion.Web" />
```

Result:

| Artifact | Contains / lists Web |
| --- | --- |
| `Assimalign.Cohesion.Database.nupkg` `.nuspec` dependencies | No |
| `Assimalign.Cohesion.App.Database.Ref.nupkg` + `FrameworkList.xml` | No |
| `Assimalign.Cohesion.App.Database.Runtime.<rid>.nupkg` + `RuntimeList.xml` | Yes |

Privacy is enforced at the package boundary, not in the type system. The
library can `using Assimalign.Cohesion.Web;` freely, and the compiler
will let any `public` member return or accept a Web type. A leak surfaces
downstream as a `CS0012` ("type is defined in an assembly that is not
referenced") for the consumer, because Web isn't in their assembly
graph. To keep privacy honest:

- Cross-library uses of the private dep stay `internal` inside the
  owning library. If hosts need to configure the underlying piece
  (timeouts, ports, etc.), expose proxy types on the public side and
  translate internally.
- Forgetting `CohesionPrivateProjectReference` (using
  `CohesionProjectReference` instead) leaks the private dep as a
  transitive `<dependency>` of the public `.nupkg`. Forgetting
  `CohesionFrameworkPrivateAssembly` means the private DLL isn't in the
  Runtime pack, so the host crashes at run time the moment the private
  path is exercised.

### Adding a new framework + SDK domain

```powershell
# 1. Create the resources/<Name>/ folder if it doesn't exist.

# 2. Generate the SDK + Framework scaffold (7 files):
pwsh installer/scripts/New-CohesionDomainScaffold.ps1 -Name <Name>

# 3. Wire it up (currently manual; mirrors the existing entries):
#    a. Add a KnownFrameworkReference block to
#       sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props
#    b. Add a property-conditioned ItemGroup to
#       frameworks/Assimalign.Cohesion.App.props
#    c. Add the framework name to $cohesionFrameworks and the SDK to the
#       SDK projects list in installer/scripts/Install-Local.ps1
#    d. Add the new Refs + Runtime folder/project entries to
#       frameworks/Assimalign.Cohesion.Frameworks.slnx

# 4. Verify locally:
pwsh installer/scripts/Install-Local.ps1
```

The scaffold is idempotent: re-running the script skips anything already on
disk.

### Versioning

`$(CohesionVersion)` is the single source of truth and lives in
`build/Targets/Build.Version.props`. Every Cohesion package - SDK, Ref pack,
Runtime pack, library - shares this version. Bumping is a one-line edit.

For framework projects, `frameworks/Directory.Build.props` sets
`<VersionPrefix>$(CohesionVersion)</VersionPrefix>` so Microsoft.NET.Sdk's
default `VersionPrefix=1.0.0` doesn't win. Don't remove that line - it's
the only thing keeping framework `.nupkg` versions aligned with the SDK.

### Dev loop: `Install-Local.ps1`

Packs all 19 SDKs + 19 framework families (Ref + per-RID Runtime each) into
the in-tree feed at `_out/packages/`. The repo's `nuget.config` maps
`Assimalign.Cohesion.*` to that feed, so any consumer csproj under the repo
(or under a sibling repo whose `nuget.config` points back here) restores
from the local build.

Flags worth knowing:

- `-Configuration Release` - Release pack (default Debug)
- `-Rids 'win-x64','linux-x64',...` - cross-RID runtime packs (default: host RID)
- `-SkipSdks` / `-SkipFramework` - iterate one half without the other
- `-Force` - bypass the locked-DLL check (see "Recovery from a wedged dev loop")

The script's first step is a guard: it tries to open every cached
`Assimalign.Cohesion.Sdk[.Family].Tasks.dll` under `~/.nuget/packages/` for
exclusive write. If anything (most commonly Visual Studio) has the DLL
loaded, it refuses to proceed rather than producing a half-replaced cache.
Close VS, then re-run.

### Recovery from a wedged dev loop

If `Install-Local.ps1` aborts with "Cached Cohesion SDK Tasks DLL is
file-locked":

1. Close Visual Studio fully (`Get-Process devenv` should return nothing).
2. Optional: kill any leftover dotnet host that still has the DLL loaded
   (the script tells you the PID).
3. Re-run `Install-Local.ps1`.

If a consumer build complains about an `Assimalign.Cohesion.Sdk` it can't
resolve, the usual cause is a missing pin in `global.json`'s `msbuild-sdks`
block. `<Import Sdk="X">` chains (used inside Sdk.Web/Sdk.Database to chain
to the base Sdk) do not honor inline-version syntax - the consumer's
`global.json` must pin every SDK that appears anywhere in the chain.

### CI: `.github/workflows/framework.yml`

Three-stage pipeline:

1. **Pack** (single Linux job, ~3-8 min) - runs `Install-Local.ps1` with all
   7 declared RIDs, uploads every `.nupkg` as the `cohesion-packages` artifact.
2. **Smoke-test** (matrix: ubuntu/windows/macos) - materializes three inline
   consumer csprojs (`SmokeTest.App`, `SmokeTest.Web`, `SmokeTest.Database`),
   builds each against its targeting pack, publishes self-contained against
   its per-RID runtime pack, runs the produced apphost. Validates that every
   layer of the chain works on every supported OS.
3. **Publish** (`needs: [pack, smoke-test]`, only on `main`) - pushes every
   `.nupkg` to GitHub Packages via `.github/scripts/Publish-Nupkg.ps1`.

The GitHub Packages feed is treated as a **QA/UAT staging registry**: every
push to `main` on the same `$(CohesionVersion)` REPLACES the previous
publish. The helper script does `GET versions` → `DELETE` the matching
version → `dotnet nuget push`, with `--skip-duplicate` deliberately omitted
so a failed replacement turns CI red rather than silently leaving the old
version on the feed. Consumers always restore whatever `main` last pushed.

The library/resource workflows (`library-*.yml`, `resource-*.yml`) follow
the same publish pattern via the shared composite action at
`.github/actions/build/action.yml`. Each declares `permissions: packages:
write` so the workflow's `GITHUB_TOKEN` can both push and delete on the feed.

### File layout reference

```
frameworks/
├── Assimalign.Cohesion.App.props          ← framework membership manifest
├── Assimalign.Cohesion.App.targets        ← collection + manifest writer logic
├── Directory.Build.props                  ← sets VersionPrefix for framework projects
└── Assimalign.Cohesion.App[.Domain]/
    ├── Refs/src/...Refs.csproj            ← produces the .Ref targeting pack
    └── Runtime/src/...Runtime.csproj      ← produces the .Runtime.<rid> runtime pack(s)

sdks/
└── Assimalign.Cohesion.Sdk[.Domain]/
    ├── Sdk/Sdk.props                      ← what consumers see first
    ├── Sdk/Sdk.targets
    ├── Targets/...FrameworkReference.props ← base SDK only: KnownFrameworkReference list
    ├── Targets/Sdk.<Domain>.props          ← per-domain build hooks (mostly empty today)
    ├── Targets/Sdk.<Domain>.targets
    └── Tasks/...Tasks.csproj              ← code-generation task DLL

installer/scripts/
├── Install-Local.ps1                      ← dev loop: pack everything locally
├── New-CohesionDomainScaffold.ps1         ← scaffold a new SDK + Framework pair
├── Publish-Nupkg.ps1                      ← delete-then-push helper for GitHub Packages
└── Cleanup-PriorRegistrations.ps1         ← one-shot cleanup for old MSI-based registrations
```

### Architecture rules

1. **Never hardcode a version in a chained `<Import Sdk>` element.** That
   syntax (`Sdk="X/version"`) is not honored on `<Import Sdk>`; only on
   `<Project Sdk>`. The consumer's `global.json` pins the version.
2. **Never put a framework's full content under one csproj.** Each Cohesion
   library is its own project under `libraries/` or `resources/`; the
   framework's Runtime project is purely a packaging shell whose
   `<CohesionProjectReference>` items come from the declarative `App.props`.
3. **Never bypass the `$(CohesionVersion)` chain.** No per-project `<Version>`
   overrides. If a project needs a different version, that's a sign it
   should ship outside the framework, not inside it.
4. **`FrameworkList.xml` and `RuntimeList.xml` are build artifacts.** They're
   in `.gitignore`. The collection target in `App.targets` regenerates them
   on every pack; never edit them by hand.
5. **The base `Sdk` registers every framework's `KnownFrameworkReference`.**
   Adding the registration in the chained SDK (`Sdk.Web`, etc.) doesn't
   propagate to consumers using only the base SDK; everything goes through
   the base.

## General Rules

### ✅ Required Patterns

1. **Always use file-scoped namespaces**
   ```csharp
   namespace Assimalign.Cohesion.Database;
   
   public class DatabaseEngine { }
   ```

2. **Use `CohesionProjectReference` for internal project dependencies**
   ```xml
   <CohesionProjectReference Include="Assimalign.Cohesion.Core" />
   ```
   - When the dependency is an internal implementation detail that should
     NOT flow to consumers of the library's `.nupkg`, use
     `CohesionPrivateProjectReference` instead, and pair it with a
     matching `CohesionFrameworkPrivateAssembly` entry in
     `frameworks/Assimalign.Cohesion.App.props`. See "Cross-resource
     dependencies" under Framework + SDK Architecture for the full pattern.
   ```xml
   <CohesionPrivateProjectReference Include="Assimalign.Cohesion.Web" />
   ```

3. **Use `CohesionPackageReference` for NuGet packages**
   ```xml
   <CohesionPackageReference Include="Newtonsoft.Json" />
   ```

4. **Namespace MUST match assembly name exactly**
   - Assembly: `Assimalign.Cohesion.Database.Documents`
   - Namespace: `namespace Assimalign.Cohesion.Database.Documents;`

5. **All libraries MUST target `net10.0`**
   ```xml
   <PropertyGroup>
     <TargetFramework>net10.0</TargetFramework>
   </PropertyGroup>
   ```
   - However this can be generally disregarded as the target framework is managed in `build\Targets\TargetFramework.props`

6. **Preview language features MUST be enabled**
   ```xml
   <PropertyGroup>
     <LangVersion>Preview</LangVersion>
     <EnablePreviewFeatures>true</EnablePreviewFeatures>
   </PropertyGroup>
   ```

7. **Markdown files MUST use uppercase snake casing**
   - ✅ `README.md`, `CONTRIBUTING.md`, `LICENSE`
   - ❌ `readme.md`, `contributing.md`
   - Exception: API reference files under `docs/Assembly/` may mirror namespace and type names directly, for example `docs/Assembly/System.IO/Glob.md`

8. **Prefer direct throws or .NET 10 extension type methods over ThrowHelpers**
   - Use direct `throw` statements or framework guard APIs when the logic is local
   - If reusable throw behavior is needed, implement it as a .NET 10 extension type method in `Extensions/`

9. **Use the .NET 10 `extension(...)` syntax for extension members**
   - Define new extension members with `extension(...)` blocks
   - Do not use the legacy `this` parameter syntax for new extension members

10. **Scope exception roots to a library or service family**
   - Prefer local roots such as `FileSystemException`, `HttpException`, or `DatabaseException` when a service area needs a shared exception base
   - Area-root exceptions should inherit directly from `Exception` or `SystemException` unless there is a strong BCL reason to do otherwise
   - Keep exception inheritance local to the owning area instead of introducing framework-wide base exception dependencies

### ❌ Forbidden Patterns

1. **NEVER use block-scoped namespaces**
   ```csharp
   // ❌ WRONG
   namespace Assimalign.Cohesion.Database
   {
       public class DatabaseEngine { }
   }
   ```

2. **NEVER use relative paths in project references**
   ```xml
   <!-- ❌ WRONG -->
   <ProjectReference Include="..\..\Core\Assimalign.Cohesion.Core\src\Assimalign.Cohesion.Core.csproj" />
   ```

3. **NEVER add package references without adding to centralized versions**
   - First add to `build/Targets/PackageReferences.targets`
   - Then use `CohesionPackageReference`

4. **NEVER use `PackageReference` directly**
   ```xml
   <!-- ❌ WRONG -->
   <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
   ```
5. **NEVER use any of the following packages**
   - Any `Microsoft.Extensions.*`

6. **NEVER create public classes without XML documentation**
   ```csharp
   // ❌ WRONG - Missing documentation
   public interface IDatabase { }
   
   // ✅ CORRECT
   /// <summary>
   /// Provides database access functionality.
   /// </summary>
   public interface IDatabase { }
   ```

7. **NEVER introduce `ThrowHelper` or `ThrowHelpers` types**
   - Do not add helper classes whose primary purpose is throwing exceptions
   - When touching existing usages, migrate them toward direct throws or .NET 10 extension type methods

8. **NEVER declare new extension members with the legacy `this` parameter syntax**
   ```csharp
   // ❌ WRONG
   public static class DatabaseExtensions
   {
       public static IServiceCollection AddDatabase(this IServiceCollection services)
       {
           return services;
       }
   }
   ```

9. **NEVER introduce new framework-wide base exception types for unrelated areas**
   - Do not create or revive cross-framework roots such as `CohesionException` or `NetworkException`
   - Unrelated libraries should not depend on a shared exception ancestry just to satisfy framework conventions

## Naming Conventions

### Types

**Interfaces:** Prefix with `I`
```csharp
public interface IDatabase { }
public interface IConfigurationProvider { }
```

**Classes:** PascalCase, descriptive nouns
```csharp
public class DatabaseEngine { }
public class ConfigurationBuilder { }
```

**Exceptions:** Suffix with `Exception`
```csharp
public class DatabaseConnectionException : CohesionException { }
```

**Extension Classes:** Suffix with `Extensions`
```csharp
public static class ServiceCollectionExtensions { }
```

### Members

**Methods:** PascalCase, start with verb
```csharp
public void ExecuteQuery() { }
public async Task<T> GetAsync<T>() { }
```

**Properties:** PascalCase, descriptive nouns
```csharp
public string ConnectionString { get; set; }
public int MaxRetries { get; init; }
```

**Fields:** camelCase with `_` prefix for private fields
```csharp
private readonly string _connectionString;
private int _retryCount;
```

**Constants:** PascalCase for public, camelCase for private
```csharp
public const int DefaultTimeout = 30;
private const int maxRetries = 3;
```

**Parameters:** camelCase
```csharp
public void Execute(string connectionString, int timeout) { }
```

**Local Variables:** camelCase
```csharp
var connectionString = "...";
int retryCount = 0;
```

## Code Organization

### Folder Structure Rules

Libraries MUST follow this structure:
```
libraries/{Category}/Assimalign.Cohesion.{Library}/
├── src/
│   ├── Abstractions/      # Interfaces only
│   ├── Extensions/        # Extension members
│   ├── Internal/          # Internal implementation
│   ├── Exceptions/        # Custom exceptions
│   ├── ValueObjects/      # Value types
│   └── [Feature folders]
├── docs/
│   ├── OVERVIEW.md        # Project overview
│   ├── DESIGN.md          # Project design notes
│   └── Assembly/          # API reference by namespace and type
└── tests/
    ├── TestObjects/       # Test fixtures
    └── Shared/            # Shared test code
```

### File Organization Rules

1. **One public type per file** (exceptions: nested types, related enums)
2. **File name MUST match primary type name**
   - `DatabaseEngine.cs` contains `class DatabaseEngine`
   - Exception: when several files represent variants of the same root abstraction, prefer grouped root-first filenames so related files sort together
   - Example: use `Http2Frame.Header.cs` and `Http2Frame.Ping.cs` instead of `HeaderHttp2Frame.cs` and `PingHttp2Frame.cs`
   - This grouped naming should be used for implementation families that share the same abstraction root even if the concrete type name remains variant-first
3. **Extension members** in partial classes in `Extensions/` folder using `extension(...)`
4. **Test files** named `{Feature}Tests.cs`

### Using Directives

**Order:**
1. System namespaces
2. Third-party namespaces
3. Cohesion namespaces
4. Blank line before code

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database;
```

**Never use global usings or project-level `<Using Include="...">` items. Add explicit `using` directives in each file instead.**

## Access Modifiers

### Default Visibility Rules

1. **Implementation classes:** `internal` by default
   ```csharp
   internal class DatabaseConnectionPool { }
   ```

2. **Public APIs:** Use interfaces
   ```csharp
   public interface IDatabase { }
   internal class Database : IDatabase { }
   ```

3. **Extension containers:** Always `public static`, with members declared inside `extension(...)`
   ```csharp
   public static class DatabaseExtensions
   {
       extension(IServiceCollection services)
       {
           public IServiceCollection AddDatabase()
           {
               services.AddSingleton<IDatabase, Database>();
               return services;
           }
       }
   }
   ```

4. **Nested types:** Match outer type visibility unless explicitly different

## Documentation Standards

### Project Level Documentation Requirements

Every project with `src/` and `tests/` should also have a sibling `docs/` folder.

Required project-level documentation:
- `docs/OVERVIEW.md` describing the project purpose, scope, dependencies, and usage at a high level
- `docs/DESIGN.md` describing architecture, important design choices, lifecycle behavior, extension points, operational concerns, and known constraints
- `docs/Assembly/` containing API reference material organized by namespace and type

Assembly documentation layout:
- Namespace folders under `docs/Assembly/` should mirror the documented namespace, for example `docs/Assembly/System.IO/`
- Type documentation files inside those folders should mirror the type name, for example `docs/Assembly/System.IO/Glob.md`
- Assembly API docs are the exception to the uppercase-markdown naming rule because they intentionally mirror CLR namespace and type names
- API reference docs should outline the public surface area, constructor or factory behavior, methods, properties, exceptions, and usage notes for the documented type

### Area Level Documentation Requirements

Each major area root should contain a `README.md` that provides an overview of that area.

Examples:
- `resources/Web/README.md`
- `resources/Database/README.md`
- `libraries/Core/README.md`

Area-level `README.md` files should summarize:
- the purpose of the area
- the major projects or services it contains
- how the area fits into the L1, L2, and L3 layering model
- important dependencies on other areas
- links to project-level `OVERVIEW.md` and `DESIGN.md` files where relevant

### XML Documentation Requirements

**Public APIs MUST have:**
- `<summary>` - Brief description
- `<param>` - For each parameter
- `<returns>` - For non-void methods
- `<exception>` - For thrown exceptions
- `<remarks>` - For additional details (optional)

**Example:**
```csharp
/// <summary>
/// Executes a database query asynchronously.
/// </summary>
/// <param name="query">The SQL query to execute.</param>
/// <param name="parameters">Query parameters to bind.</param>
/// <param name="cancellationToken">Cancellation token for the operation.</param>
/// <returns>A task representing the query result.</returns>
/// <exception cref="DatabaseConnectionException">Thrown when connection fails.</exception>
/// <remarks>
/// This method automatically retries on transient failures up to 3 times.
/// </remarks>
public async Task<QueryResult> ExecuteAsync(
    string query,
    Dictionary<string, object> parameters,
    CancellationToken cancellationToken = default)
{
    // Implementation
}
```

**Internal types MAY omit XML docs** but should use code comments for complex logic

## Testing Standards

### Test Naming

**Test class:** `{Feature}Tests`
```csharp
public class DatabaseConnectionTests { }
```

**Test methods:** `{Method}_{Scenario}_{ExpectedBehavior}`
```csharp
[Fact]
[DisplayName("Cohesion Test [Database] - Execute: Should retry on transient failure")]
public async Task Execute_OnTransientFailure_ShouldRetry()
{
    // Test implementation
}
```

### Test Structure

**Use AAA pattern:**
```csharp
[Fact]
public void Cache_OnMiss_ShouldReturnNull()
{
    // Arrange
    var cache = new MemoryCache();
    
    // Act
    var result = cache.Get("nonexistent");
    
    // Assert
    result.ShouldBeNull();
}
```

### Test Assertions

**Use Shouldly - it is the single assertion library for this repository:**
```csharp
// ✅ Shouldly
result.ShouldNotBeNull();
result.Count.ShouldBe(5);
result.ShouldContain(x => x.Id == "123");

// ❌ FluentAssertions (forbidden - v8+ moved to a paid commercial license)
result.Should().NotBeNull();

// ❌ Traditional Assert (avoid)
Assert.NotNull(result);
Assert.Equal(5, result.Count);
```

## Pattern Requirements

### Interface-First Design

**Always define interfaces for public APIs:**
```csharp
// 1. Define interface
public interface ICache
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value);
}

// 2. Implement internally
internal class MemoryCache : ICache
{
    public Task<T?> GetAsync<T>(string key) { /* ... */ }
    public Task SetAsync<T>(string key, T value) { /* ... */ }
}

// 3. Register via extension member
public static class CacheExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddMemoryCache()
        {
            return services.AddSingleton<ICache, MemoryCache>();
        }
    }
}
```

### Interface-First with a Guided Abstract Base

Public APIs stay interface-first — the interface is the contract consumers depend on. In addition, ship a companion **public `abstract` base class that explicitly implements the interface** and forwards each member to a strongly-typed `protected`/`public abstract` member. The base guides implementers toward the intended shape (member signatures, return types, lifecycle) while the interface remains the canonical public surface.

- The interface is the contract; consumers program against it.
- The abstract base is the guided implementation path; concrete types derive from it and stay `internal` where possible.
- Where the base can expose a richer, concrete-typed member (e.g., an `AcceptAsync` returning the concrete `Connection` instead of `IConnection`), implement that interface member **explicitly** (`ReturnType IFoo.Member(...)`) and forward to a strongly-typed `abstract`/`virtual` member the derived type overrides. Members without a richer counterpart are declared `public`/`protected abstract` directly. Explicit implementation keeps the interface the canonical surface while the base guides the implementer.

```csharp
// 1. Contract — what consumers depend on
public interface IConnectionListener
{
    ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

// 2. Guided abstract base — explicit interface impl forwards to a strongly-typed member.
//    Declare the typed member public so holders of the concrete type get the richer signature
//    without casting to the interface.
public abstract class ConnectionListener : IConnectionListener
{
    /// <summary>Implementers override this; the concrete return type guides the implementation.</summary>
    public abstract ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default);

    async ValueTask<IConnection> IConnectionListener.AcceptAsync(CancellationToken cancellationToken)
        => await AcceptAsync(cancellationToken).ConfigureAwait(false);
}

// 3. Concrete implementation derives from the base
internal sealed class TcpConnectionListener : ConnectionListener
{
    public override ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default) { /* ... */ }
}
```

### Async/Await Rules

1. **Async methods MUST have `Async` suffix**
   ```csharp
   public async Task<string> GetDataAsync() { }
   ```

2. **Always accept `CancellationToken` for async operations**
   ```csharp
   public async Task<T> GetAsync(string key, CancellationToken cancellationToken = default)
   {
       // Implementation
   }
   ```

3. **Avoid `async void`** except for event handlers
   ```csharp
   // ❌ WRONG
   public async void Process() { }
   
   // ✅ CORRECT
   public async Task ProcessAsync() { }
   ```

### Exception Handling

1. **Catch specific exceptions, not `Exception`**
   ```csharp
   try
   {
       await database.ConnectAsync();
   }
   catch (DatabaseConnectionException ex)
   {
       // Handle connection failure
   }
   ```

2. **Use custom exceptions for domain errors**
   ```csharp
   public class InvalidConfigurationException : Exception
   {
       public InvalidConfigurationException(string key) 
           : base($"Configuration key '{key}' is invalid or missing.") { }
   }
   ```
   - When multiple implementations within the same area need a shared root, define an area-specific base such as `FileSystemException`
   - Avoid cross-framework exception roots that force unrelated libraries to share the same ancestry

3. **Preserve stack trace when rethrowing**
   ```csharp
   // ✅ CORRECT
   catch (Exception ex)
   {
       logger.LogError(ex, "Operation failed");
       throw;
   }
   
   // ❌ WRONG - Loses stack trace
   catch (Exception ex)
   {
       throw ex;
   }
   ```

4. **Avoid `ThrowHelper` patterns**
   - Prefer direct `throw` statements for local guard clauses
   - If throwing logic must be reused, use a .NET 10 extension type method instead of a helper class

## Performance Guidelines

### Memory Allocation

1. **Prefer `ValueTask<T>` for frequently called async methods**
   ```csharp
   public ValueTask<T?> GetFromCacheAsync(string key)
   {
       if (cache.TryGetValue(key, out var value))
           return new ValueTask<T?>(value);
       
       return LoadFromDatabaseAsync(key);
   }
   ```

2. **Use `Span<T>` and `Memory<T>` for buffer operations**

3. **Avoid allocations in hot paths**

### AOT Compatibility

All libraries MUST be AOT-compatible:

```xml
<PropertyGroup>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

**Avoid:**
- Reflection-based serialization
- Dynamic code generation at runtime
- `Assembly.LoadFrom()`
- Runtime type inspection without source generators

## Version Control

### Commit Messages

Follow conventional commits format:

```
type(scope): subject

body

footer
```

**Types:**
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `refactor`: Code refactoring
- `test`: Test additions/changes
- `chore`: Build/tooling changes

**Examples:**
```
feat(database): add connection pooling support
fix(cache): resolve memory leak in expiration logic
docs(readme): update build instructions
refactor(config): simplify provider registration
test(hosting): add lifecycle event tests
chore(build): update to .NET 10.0.101
```

### Branch Naming

- `main` - Production-ready code
- `development` - Integration branch
- `feature/{name}` - New features
- `fix/{name}` - Bug fixes
- `docs/{name}` - Documentation updates

## Code Review Checklist

Before submitting code, ensure:

- [ ] File-scoped namespaces used
- [ ] `CohesionProjectReference` used for internal dependencies
- [ ] `CohesionPackageReference` used for packages
- [ ] XML documentation on all public APIs
- [ ] Tests added/updated
- [ ] Exception roots stay scoped to the owning library or service area
- [ ] No new `ThrowHelper` or `ThrowHelpers` types introduced
- [ ] New extension members use `extension(...)` instead of the legacy `this` parameter syntax
- [ ] Async methods have `Async` suffix
- [ ] `CancellationToken` parameter included in async methods
- [ ] No `async void` methods (except event handlers)
- [ ] No hardcoded package versions in project files
- [ ] Markdown files use uppercase names
- [ ] Code follows existing patterns in the category

---

**Canonical source:** `AGENTS.md`

**Copilot mirror:** `.github/copilot-instructions.md` should stay aligned with this file and should not override it.
