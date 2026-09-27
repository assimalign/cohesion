---
paths:
  - "**/*.cs"
  - "**/*.csproj"
---

# General Rules

Required/forbidden C# patterns for this repo. The files in `.claude/rules/` are the canonical coding standard.

## Required patterns

### File-scoped namespaces
```csharp
namespace Assimalign.Cohesion.Database;

public class DatabaseEngine { }
```

### `CohesionProjectReference` for internal deps
```xml
<CohesionProjectReference Include="Assimalign.Cohesion.Core" />
```

### `CohesionPackageReference` for NuGet packages
```xml
<CohesionPackageReference Include="Newtonsoft.Json" />
```
Version goes in `build/Targets/Build.References.Packages.targets` first.

### Every project pins its `RootNamespace`, and code declares it
Every csproj — library, test, sample, fixture, tool — declares `<RootNamespace>` explicitly in its
own project file, never inherited from `$(MSBuildProjectName)`. **COHNS001**
(`build/Targets/Build.Rules.targets`) fails the build of any project that omits it; `dotnet new`
template content under `tooling/templates/**/content/` is the only exemption, because those files
become customer projects.

```xml
<PropertyGroup>
	<RootNamespace>Assimalign.Cohesion.Database.Documents</RootNamespace>
</PropertyGroup>
```

- The pin is normally the assembly name. A family that shares one namespace pins the family name
  (`Assimalign.Cohesion.Http.Cookies` → `Assimalign.Cohesion.Http`); a project whose code already
  declares a different namespace pins what the code declares rather than renaming public API.
- `Abstractions/`, `Exceptions/`, `Extensions/`, and `ValueObjects/` types declare exactly the
  `RootNamespace` — never `.Abstractions`, `.Exceptions`, `.Extensions`, or `.ValueObjects`.
- Internal types declare `{RootNamespace}.Internal`, whichever `Internal/` subfolder holds them.
- Everything else defaults to the `RootNamespace`.

### Target framework
- Libraries target `net10.0` — but the target framework is centrally managed via `TargetFrameworkLatest` in `build/Targets/Build.TargetFramework.props`, so per-project overrides are normally not needed.
- Sanctioned exception: `analyzers/` projects (Roslyn analyzers/codefixes/generators) target `netstandard2.0` with `IsAotCompatible=false` via `analyzers/Directory.Build.props`, because Roslyn components load inside the compiler.

### Preview language features
```xml
<PropertyGroup>
  <LangVersion>Preview</LangVersion>
  <EnablePreviewFeatures>true</EnablePreviewFeatures>
</PropertyGroup>
```
These are also centrally managed. Don't duplicate per project unless the project genuinely needs to deviate.

### Markdown files use UPPERCASE
- ✅ `README.md`, `CONTRIBUTING.md`, `LICENSE`
- ❌ `readme.md`, `contributing.md`
- Exception: files whose names are fixed by external tooling keep their conventional casing (e.g., `.github/pull_request_template.md`, files under `.claude/**`).
- API reference needs no exception: **folders** under `docs/Assembly/` mirror CLR namespace/type names, and each type's page is `docs/Assembly/<Namespace>/<Type>/OVERVIEW.md`.

### Direct throws or .NET 10 extension type methods, not `ThrowHelper`
- Use direct `throw` statements or framework guard APIs (e.g., `ArgumentNullException.ThrowIfNull`) when the logic is local.
- If reusable throw behavior is needed, implement as a .NET 10 extension type method in `Extensions/`.

### `.NET 10 extension(...)` syntax for extension members
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
The legacy `this T param` syntax is forbidden in new code.

### Scope exception roots to a library or service area
- Use local roots like `FileSystemException`, `HttpException`, `DatabaseException` when an area needs a shared base.
- Area-root exceptions inherit directly from `Exception` or `SystemException` unless there's a strong BCL reason otherwise.
- Keep exception inheritance local to the owning area.

## Forbidden patterns

### Block-scoped namespaces
```csharp
// ❌ WRONG
namespace Assimalign.Cohesion.Database
{
    public class DatabaseEngine { }
}
```

### Relative paths in project references
```xml
<!-- ❌ WRONG -->
<ProjectReference Include="..\..\Core\Assimalign.Cohesion.Core\src\Assimalign.Cohesion.Core.csproj" />
```

### Adding package references without centralized versions
- Always add to `build/Targets/Build.References.Packages.targets` first.
- Then use `CohesionPackageReference`.

### Raw `PackageReference`
```xml
<!-- ❌ WRONG -->
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
```

### Any `Microsoft.Extensions.*` package
Standing architectural commitment. No exceptions without explicit user confirmation that they understand the impact.

### Public classes without XML documentation
```csharp
// ❌ WRONG
public interface IDatabase { }

// ✅ CORRECT
/// <summary>
/// Provides database access functionality.
/// </summary>
public interface IDatabase { }
```

### `ThrowHelper` / `ThrowHelpers` types
- Do not add helper classes whose primary purpose is throwing exceptions.
- When touching existing usages, migrate toward direct throws or extension type methods.

### Legacy `this` extension syntax in new code
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

### Framework-wide base exception types for unrelated areas
- No `CohesionException`, `NetworkException`, or similar cross-framework roots.
- Unrelated libraries should not share exception ancestry just for convention.

### Primary constructors on classes and structs
```csharp
// ❌ WRONG
internal sealed class DocumentPlanner(DocumentCatalog catalog) { }

// ✅ CORRECT
internal sealed class DocumentPlanner
{
    private readonly DocumentCatalog _catalog;

    public DocumentPlanner(DocumentCatalog catalog)
    {
        _catalog = catalog;
    }
}
```
Captured parameters become explicit `_camelCase` fields assigned in an explicit constructor.
Positional **records** (`record Foo(int X)`, `record struct Foo(int X)`) are not primary
constructors in this sense: their parameters generate properties, `Deconstruct`, and positional
patterns, so they stay.

## Naming conventions

### Types
| Kind | Convention | Example |
|---|---|---|
| Interface | `I` prefix | `IDatabase`, `IConfigurationProvider` |
| Class | PascalCase, noun | `DatabaseEngine`, `ConfigurationBuilder` |
| Exception | `Exception` suffix | `DatabaseConnectionException` |
| Extension container | `Extensions` suffix | `ServiceCollectionExtensions` |

### Members
| Kind | Convention | Example |
|---|---|---|
| Method | PascalCase, verb-first | `ExecuteQuery`, `GetAsync` |
| Property | PascalCase, noun | `ConnectionString`, `MaxRetries` |
| Private field (instance, `static`, or `readonly`; no exceptions) | `_camelCase` | `_connectionString`, `_retryCount`, `_defaultTimeout` |
| Public const | PascalCase | `DefaultTimeout` |
| Private const | camelCase | `maxRetries` |
| Parameter | camelCase | `connectionString`, `timeout` |
| Local variable | camelCase | `connectionString`, `retryCount` |

## Code organization

### Library folder structure
```
libraries/{Category}/Assimalign.Cohesion.{Library}/
├── src/
│   ├── Abstractions/      # Public interfaces and abstract classes — flat
│   ├── Exceptions/        # Public exceptions and their {Name}ErrorCode enums — flat
│   ├── Extensions/        # Public extension containers — flat
│   ├── Internal/          # Every internal type; subfolders allowed
│   │   ├── EventSource/   # EventSource-derived types
│   │   └── Exceptions/    # Internal exceptions
│   ├── ValueObjects/      # Value objects, generated and hand-written — flat
│   ├── Properties/        # AssemblyInfo.cs (and resx designers)
│   └── [Feature folders]  # Other public types
├── shared/                # Source compiled into sibling assemblies — see "Shared source"
├── docs/
│   ├── OVERVIEW.md
│   ├── DESIGN.md
│   └── Assembly/          # API reference by namespace and type
└── tests/
    ├── TestObjects/
    └── Shared/
```

These folder rules apply to every shipped `src/` project (`libraries/`, `resources/`, `sdks/*/Tasks/`,
`analyzers/`, `tooling/`). Test, sample, fixture, and example projects keep the `tests/` layout in
`testing.md`; the naming and constructor rules below still apply to them.

| Folder | Holds | Nesting | Namespace |
|---|---|---|---|
| `Abstractions/` | Public interfaces and public `abstract` classes/records | Flat — no subfolders | `RootNamespace` |
| `Exceptions/` | Public exception types; the `{Name}ErrorCode` enum that pairs with an exception root | Flat | `RootNamespace` |
| `Extensions/` | Public `static` classes that declare `extension(...)` members | Flat | `RootNamespace` |
| `ValueObjects/` | Public value objects: structs / record structs with value equality (`IEquatable<Self>`), including every `CohesionValueType` (its `Include` path is `ValueObjects\<Name>.cs`) | Flat | `RootNamespace` |
| `Internal/` | Every `internal` type, whatever its kind | Subfolders allowed: `Internal/EventSource/` for the assembly's one `EventSource` type (`event-source.md`), `Internal/Exceptions/` for internal exceptions, plus feature folders | `{RootNamespace}.Internal` |
| `Properties/` | `AssemblyInfo.cs`; assembly-level attributes live here, not in the csproj | — | — |
| `System/` | BCL-namespace extensions (`namespace System.*`) | Mirrors the BCL namespace | `System.*` — **only** in `Assimalign.Cohesion.Core` |

Precedence when a type matches more than one row: `internal` wins (an internal exception goes to
`Internal/Exceptions/`), then exceptions, then abstractions. One file, one category: a secondary
type with a category of its own gets its own file in its own folder; a related enum or delegate
without one stays beside its primary type.

**Error codes over exception sprawl.** Prefer one `abstract` exception root per library plus a
`{Name}ErrorCode` enum in `Exceptions/` over a separate exception type per failure; consumers can
still derive their own exception types from the root.

Two narrow exceptions, each marked with a `// Deviates from ...` comment where it occurs:
compiler polyfills such as `IsExternalInit` keep `System.Runtime.CompilerServices` because the
compiler binds them by exact name, and `Properties/` resx designer classes keep the namespace their
resource manifest name requires.

### File organization rules
1. **One public type per file** (exceptions: nested types, related enums).
2. **File name matches primary type name** — e.g., `DatabaseEngine.cs` contains `class DatabaseEngine`.
3. **Variant families use grouped root-first naming:** `Http2Frame.Header.cs` and `Http2Frame.Ping.cs`, not `HeaderHttp2Frame.cs` and `PingHttp2Frame.cs`. The concrete type name remains variant-first; only the filename is grouped.
4. **Extension members** live in partial classes under `Extensions/` using `extension(...)`.
5. **Test files** named `{Feature}Tests.cs`.

### Using directives
**Order:**
1. `System.*` namespaces
2. Third-party namespaces
3. `Assimalign.Cohesion.*` namespaces
4. Blank line before code

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using NUlid;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database;
```

**Never use global usings or `<Using Include="..." />` items in project files. Add explicit `using` directives in each file.**

## Access modifiers

1. **Implementation classes:** `internal` by default.
   ```csharp
   internal class DatabaseConnectionPool { }
   ```
2. **Public APIs use interfaces.**
   ```csharp
   public interface IDatabase { }
   internal class Database : IDatabase { }
   ```
3. **Extension containers:** always `public static`, with members inside `extension(...)`.
4. **Nested types:** match outer type visibility unless explicitly different.
5. **Before introducing a new abstraction, check whether one already exists** in the same service root or shared library. Placeholder folders and placeholder projects are not final architecture boundaries — add projects when needed to preserve modularity and clean dependency flow.

### `InternalsVisibleTo` is for tests

A shipped library grants `InternalsVisibleTo` only to its own **test** assembly. Do not add a
grant between two shipped libraries.

Declare grants as `[assembly: InternalsVisibleTo("...")]` in `Properties/AssemblyInfo.cs`, never as
`<InternalsVisibleTo Include="..." />` items in the csproj — one place to look for every
assembly-level attribute.

Wanting one means a consumer needs a capability the producer does not expose. The grant does not
supply that capability — it hides the question, and every later reader has to reconstruct which of
the producer's internals are load-bearing for whom. Resolve it at the design level instead:

- **Give the producer a public seam** for what the consumer legitimately needs, and keep the rest
  internal. This is the resolution of record for the host's run wrapper
  (`DEVELOPER_EXPERIENCE_DESIGN.md` R6: `IHostRunner`/`IHostRun`/`IHostRunObserver`, explicitly
  "no `InternalsVisibleTo`").
- **Move the work to the package that owns the type.** If the consumer is manipulating the
  producer's data structures, the operation usually belongs on the producer's side of the line.
- **Restructure so the consumer does not need it** — often the smallest change of the three.
  Precedent: `Database.Sql` reconstructed `Database.Sql.Language`'s AST nodes to substitute
  subquery results, which needed their internal constructors. Resolving each subquery by node
  identity at evaluation time removed the reconstruction, the grant, and a whole rewriter class.

Adding a public API to the producer *purely* to serve one consumer is not a way around this — that
is the one-off accretion the abstraction rule above already rejects. If none of the three options
fits, the boundary itself is wrong: raise it rather than granting visibility.

**Zero shipped-to-shipped grants remain**, measured 2026-09-18 after Phase 19.
Four dead grants were removed in `e8577dfe`; the remaining fourteen were resolved
with public composition seams, producer-owned operations, and **shared source**
(next section). The IdentityModel family compiles its static materialization and
endpoint-validation helpers from one source file per helper into each assembly
that needs them, and the Connections drivers compile their pipe plumbing the same
way; the public `ProtocolEndpoint` model remains defined in its owning assembly alone.

To re-measure, exclude `obj/` and `bin/` and everything targeting a `*.Tests` assembly:

```bash
grep -rn "InternalsVisibleTo" --include=*.cs --include=*.csproj . \
  | grep -vE "[/\\\\](obj|bin)[/\\\\]" | grep -vE '\.Tests"|\.Tests" />|\.Tests,'
```

A **fourth** pattern exists and is deliberately not counted above: four hosting projects grant to
*another project's* test assembly (`IdentityHub.Hosting` → `IdentityHub.Client.Tests`, and the
same in `Rezolvr.Hosting`, `SecretStore.Hosting`, `ApplicationModel.Gateway`). Those are test-only
and do not widen a shipped boundary, but they do reach past a project's own tests. Prefer a
project's own test assembly; if a sibling's tests genuinely need the internals, that is worth
questioning on the same terms as the rest of this section.

### Shared source — the `shared/` folder

Sometimes the honest answer to "two assemblies need the same internal helper" is neither a grant
nor a new public API: it is **one source file compiled into both**. A project that shares source
puts those files in a **`shared/` folder inside the project, beside `src/`, `tests/` and `docs/`**
(lowercase, matching its siblings):

```
libraries/{Category}/Assimalign.Cohesion.{Library}/
├── src/
├── shared/
├── tests/
└── docs/
```

Every assembly that wants those files names the **owning project** in its own csproj:

```xml
<ItemGroup>
  <CohesionSharedSource Include="Assimalign.Cohesion.IdentityModel" />
</ItemGroup>
```

- It resolves **by project name**, exactly like `CohesionProjectReference`, off the same
  `libraries/` + `resources/` index (`build/Targets/Build.SharedFiles.targets`). No relative paths.
- It compiles every `*.cs` under that project's `shared/` tree into the consumer, linked under a
  `shared\` node. **The folder is the unit**, not the file.
- The item lives in the **consuming csproj**, never in a `Directory.Build.targets`. Someone
  reading the csproj has to be able to see what the assembly is built from; an allowlist one
  directory up is exactly the thing that makes linked source hard to find. This applies to the
  owning project too: `shared/` is outside the csproj's `src/` directory, so a project that wants
  its own shared folder compiled in names **itself**.
- Naming a project with no `shared/` folder, or a name that is not a project, fails the build.

**The safety test.** A file in `shared/` holds only stateless static types, or types whose
instances never cross an assembly boundary. No static mutable fields, no singletons, no
`EventSource`, no lock objects, no id generators — anything with process-global identity or shared
state must stay in one assembly behind a seam. Linking duplicates the type: each assembly gets its
own distinct CLR type and its own copy of any state.

The canonical illustration is the `Connections` family, which does both at once.
`DuplexPipePair` and the pool-owning pipe options are shared source — every instance is created,
used and disposed inside the one driver that asked for them. Diagnostics are not, and cannot be:
an event source carries a process-global name and a static `Log` singleton holding counters, so
linked copies would be several providers claiming one name, each under-reporting. Each driver
therefore owns its own internal event source (`event-source.md`). The family used to route every
driver through one source in the contracts library behind public `ConnectionDiagnostics`
forwarders; that exposed the event-writing surface as public API and was retired (2026-09).

A shared-source link is a real coupling, so it is tracked in `docs/DEPENDENCIES.md` alongside the
reference flavors — regenerate the graph when you add or remove one (`documentation.md`).

Linked types keep their **owning** namespace so family callers resolve the same names; that is a
narrowly scoped, per-file deviation from the `RootNamespace` rule (markers written before 2026-09
call it the namespace-matches-assembly rule) and every shared file carries the `// Deviates from ...`
comment saying so (`deviations.md`). The `shared/` folder sits outside `src/`, so the folder rules in
"Library folder structure" do not move shared files.

## Interface-first with a guided abstract base

Public APIs stay interface-first — the interface is the contract consumers depend on. Where implementers benefit from guidance, also ship a **public `abstract` base class that explicitly implements the interface** and forwards each member to a strongly-typed `abstract`/`virtual` member:

```csharp
public interface IConnectionListener
{
    ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

public abstract class ConnectionListener : IConnectionListener
{
    // Richer concrete-typed member guides the implementer; declared public so
    // holders of the concrete type get the better signature without casting.
    public abstract ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default);

    async ValueTask<IConnection> IConnectionListener.AcceptAsync(CancellationToken cancellationToken)
        => await AcceptAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class TcpConnectionListener : ConnectionListener { /* ... */ }
```

The interface remains the canonical public surface; concrete types derive from the base and stay `internal` where possible. Use the explicit-implementation forwarding only where the base can offer a richer, concrete-typed member; members without a richer counterpart are declared `public`/`protected abstract` directly.

## Service composition

Nested host composition is intentional in this repo. Use the hosting abstractions rather than ad hoc orchestration when touching service composition. Preserve the L1/L2/L3 layering model and the service-root dependency style already established in the repo (L1 = foundation libraries and SDK/tooling, L2 = application runtime and composition, L3 = service platforms; see `docs/programs/DELIVERY_ROADMAP.md`).

## Async / await

1. **Async methods end in `Async`.**
   ```csharp
   public async Task<string> GetDataAsync() { }
   ```
2. **Always accept `CancellationToken cancellationToken = default`.**
3. **Avoid `async void`** except for event handlers.
4. **Prefer `ValueTask<T>` for frequently-called async methods** where the result is often available synchronously (e.g., cache hits).

## Exception handling

1. **Catch specific exceptions, not bare `Exception`.**
2. **Use custom exceptions for domain errors**, scoped to the owning area.
3. **Preserve stack trace when rethrowing** — `throw;`, never `throw ex;`.
4. **Avoid `ThrowHelper` patterns** — direct throws or extension type methods.

## Performance

1. Prefer `ValueTask<T>` in hot async paths.
2. Use `Span<T>` and `Memory<T>` for buffer operations.
3. Avoid allocations in hot paths.

## AOT compatibility

`<IsAotCompatible>true</IsAotCompatible>` is a hard repo-wide requirement (sanctioned exception: `analyzers/`). Preserve NativeAOT and trimming compatibility in code **and tests**. Avoid:

- Reflection-based serialization
- Dynamic code generation at runtime
- `Assembly.LoadFrom()`
- Runtime type inspection without source generators — source generators are the sanctioned path
