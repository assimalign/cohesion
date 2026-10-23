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
- Sanctioned exception: `analyzers/` projects (Roslyn analyzers/codefixes/generators) target `netstandard2.0` with `IsAotCompatible=false` via `analyzers/Directory.Build.props`, because Roslyn components load inside the compiler. Placement, naming and diagnostic tracking follow [analyzers.md](analyzers.md).

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
- Public API reference pages belong in `cohesion-docs`; public XML documentation stays in source.

### Direct throws or exception-type members, not `ThrowHelper`
- Use direct `throw` statements or framework guard APIs (e.g., `ArgumentNullException.ThrowIfNull`) when the logic is local.
- Put reusable throw behavior on the exception type as a static member, with its message in
  `Properties/ErrorMessages.resx`. An assembly owns the throw members of the exception types it owns.
- Use .NET 10 extension members in `Extensions/` only for exception types the assembly does not own.

### Exception messages are build-generated resources

- Store exception message literals and format strings in `src/Properties/ErrorMessages.resx`.
  Name each entry after the failure in PascalCase and reuse one entry for identical text.
  A name is a short noun phrase for what went wrong, about five words and 40 characters at most:
  `CookieNameInvalid`, `RequestBodyTooLarge`, `StreamingNotEnabled`. Never use the message text
  itself, or a truncation of it, as the name.
- `build/Targets/Build.ErrorMessages.targets`, imported by the shared build, configures MSBuild's
  `GenerateResource` task with `StronglyTypedLanguage`, `StronglyTypedClassName`,
  `StronglyTypedNamespace` and `StronglyTypedFileName`. It generates an **internal** `ErrorMessages`
  class in the project's `RootNamespace` under `$(IntermediateOutputPath)`. A project only adds the
  resource file; no checked-in `Designer.cs`, Visual Studio `Generator` or `LastGenOutput` metadata.
- Read `ErrorMessages.<Name>`. For parameters use
  `string.Format(CultureInfo.CurrentCulture, ErrorMessages.<Name>, args...)`, with numbered
  placeholders and the existing text preserved. Keep messages that omit request input that way.
- If exceptions are created on a normal path, including abort or cancellation reasons, cache the
  message (or format string) in a `static readonly` field. Do not look up the resource on each creation.
- Framework guards such as `ArgumentNullException.ThrowIfNull` and
  `ArgumentOutOfRangeException.ThrowIf*` that supply no message remain unchanged.
- EventSource templates, log messages and HTTP response bodies are outside this convention.
  Non-exception resources remain in `Resources.resx`, with the shared build generating their
  internal `Resources` accessor too.
- Adoption covers Http, Web and Connections and the seven existing resource projects. Other areas
  adopt it when next touched; [#1470](https://github.com/assimalign/cohesion/issues/1470) covers
  Database. No file under `resources/Database/` changes while the owner's Database PR is active.

### Validate single-valued input with generated value objects

Use a value object when a single-valued input has a validity rule and crosses a public boundary:
a name, token, path, location, HTTP field name or value, or an identifier. Make it a
`<CohesionValueType>` with `ObjectHasIsValidMethod="true"` (owner decision 60, 2026-10-14;
`docs/programs/VALUE_OBJECT_ADOPTION_SCOPE.md`).

- **One rule, one place.** The rule lives once, in the type's static `IsValid`. The property or
  parameter takes the value type, so an invalid string is refused when it is converted, before the
  setter or method runs. A non-throwing call site uses `TryCreate`.
- **Never construct to validate.** Do not build an unrelated object, or call a method, only to make
  it throw on invalid input (the retired `_ = new HttpCookie(options.CookieName)` in a try/catch).
- **Refuse `default` at the boundary.** `default` never ran `IsValid`. A boundary that accepts the
  type still refuses `default` where "no value" is invalid, as `HttpAntiforgery.Create` does.
- **Follow the grammar's case rule.** Set `ObjectComparison` to `Ordinal` or `OrdinalIgnoreCase`
  as the specification says (RFC 6265 §5.1.3 makes cookie domains case-insensitive), and cite it.
- **Numbers stay primitives.** Numeric ranges (sizes, counts, thresholds, timeouts) are guarded at
  composition with `ArgumentOutOfRangeException.ThrowIf*`. A value type is for identifiers and
  grammar-bearing strings, and each one adds about 40 public members.
- **Placement.** The type goes in `ValueObjects/` of the package that owns the concept, under its
  `RootNamespace`. The generator's metadata and surface are in
  [`docs/build/MSBUILD_COHESION_PROPS.md`](../../docs/build/MSBUILD_COHESION_PROPS.md#value-types).

### `.NET 10 extension(...)` syntax for extension members
```csharp
public static class CacheExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCache()
        {
            services.AddSingleton<ICache, MemoryCache>();
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

### Concrete types use a private nested debugger view only to filter nonpublic state

This rule applies only to C# source files in `src/` folders under `libraries/` and `resources/`:
`libraries/**/src/**/*.cs` and `resources/**/src/**/*.cs`, excluding `resources/Database/**`.
A concrete instance type there — a non-abstract, non-static class, struct, or record, regardless
of visibility — needs a debugger view **only when it has private or internal instance fields or
properties that should be omitted from the default debugging view**, including inherited state.
Private protected members count as nonpublic state. Public auto-property backing fields,
compiler-generated storage, static members, constants, and members already hidden with
`DebuggerBrowsable(DebuggerBrowsableState.Never)` do not by themselves justify a proxy.
The debugger view's own storage must not make its enclosing type qualify.

**EventSource types are excluded:** a type deriving directly or indirectly from
`System.Diagnostics.Tracing.EventSource` must not gain debugger display/proxy attributes or a
nested debugger view under this rule.

**Exception types are excluded:** any type deriving directly or indirectly from `System.Exception`
must not gain debugger display/proxy attributes or a nested debugger view under this rule.
Preserve preexisting intentional debugger attributes and views.

**Reuse a base class's debugger view first:** if a base class already supplies both an effective
`DebuggerDisplayAttribute` and `DebuggerTypeProxyAttribute`, inherit that display and proxy.
This includes abstract and concrete base classes, attributes supplied by different ancestors,
and framework base classes. Do not repeat the attributes or add a new nested view on the derived
type, even when it adds private/internal state. Preserve existing intentional redaction.

Types with only public/protected instance data, or no instance data to filter, must use the normal
debugger member view unless they inherit an existing base view; do not add a `DebuggerTypeProxy`
or nested `DebugView` to them. A useful standalone `DebuggerDisplay` is allowed, and existing
diagnostic summaries and redaction must be preserved. For types that need to filter nonpublic
state and have no existing base view, follow this pattern:

- Decorate the concrete type itself with both `DebuggerDisplayAttribute` and
  `DebuggerTypeProxyAttribute` from `System.Diagnostics`. The display gives a concise summary;
  the type proxy references that type's own nested `DebugView`.
- Declare `DebugView` as a **private nested type**, at the **bottom of the concrete type**, after
  all other members and immediately before its closing brace. Keep it in the same source file;
  do not use a top-level helper or a separate partial file.
- Give the view a public constructor that accepts the enclosing concrete instance. Keep its
  backing reference private, and expose debugger data through public read-only properties.
- For a `ref struct`, snapshot permitted non-ref-like state in that constructor instead of
  retaining the stack-only instance or its spans in a heap object.
- Project **only public or protected members of the concrete type**, including inherited ones.
  Do not expose its private or internal implementation state through the view.
- Keep inspection free of I/O, mutation, and lazy materialization. Preserve existing redaction;
  a debugger view must not reveal data that the type's diagnostic display deliberately hides.
- The nested debugger view is debugger infrastructure and does **not** require its own nested
  view or debugger attributes; this rule must not recurse.

**Everything else is excluded:** do not apply this rule under `resources/Database/**` or outside
the scoped `src/` folders. This excludes tests, shared source, fixtures, samples, examples, and
all other repository areas. Build output such as `bin/` and `obj/` is also excluded.

```csharp
using System.Diagnostics;

namespace Assimalign.Cohesion;

[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class WorkItem
{
    private readonly string _name;

    public WorkItem(string name)
    {
        _name = name;
    }

    public string Name => _name;

    private sealed class DebugView
    {
        private readonly WorkItem _item;

        public DebugView(WorkItem item)
        {
            _item = item;
        }

        public string Name => _item.Name;
    }
}
```

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
public interface ICache { }

// ✅ CORRECT
/// <summary>
/// Provides cache access functionality.
/// </summary>
public interface ICache { }
```

### `ThrowHelper` / `ThrowHelpers` types
- Do not add helper classes whose primary purpose is throwing exceptions.
- When touching existing usages, migrate toward direct throws or static members on the exception type.
  Extension members are only for exception types the assembly does not own.

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
| Interface | `I` prefix | `ICache`, `IConfigurationProvider` |
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
│   ├── Abstractions/      # Public interfaces only — flat
│   ├── Exceptions/        # Public exceptions and their {Name}ErrorCode enums — flat
│   ├── Extensions/        # Public extension containers — flat
│   ├── Internal/          # Every internal type; subfolders allowed
│   │   ├── EventSource/   # EventSource-derived types
│   │   └── Exceptions/    # Internal exceptions
│   ├── ValueObjects/      # Value objects, generated and hand-written — flat
│   ├── Properties/        # AssemblyInfo.cs and resource files (accessors generated under obj/)
│   └── [Feature folders]  # Other public types, abstract classes included (or the src/ root)
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
| `Abstractions/` | Public interfaces — and nothing else | Flat — no subfolders | `RootNamespace` |
| `Exceptions/` | Public exception types; the `{Name}ErrorCode` enum that pairs with an exception root | Flat | `RootNamespace` |
| `Extensions/` | Public `static` classes that declare `extension(...)` members | Flat | `RootNamespace` |
| `ValueObjects/` | Public value objects: structs / record structs with value equality (`IEquatable<Self>`), including every `CohesionValueType` (its `Include` path is `ValueObjects\<Name>.cs`) | Flat | `RootNamespace` |
| `Internal/` | Every `internal` type, whatever its kind | Subfolders allowed: `Internal/EventSource/` for the assembly's one `EventSource` type (`event-source.md`), `Internal/Exceptions/` for internal exceptions, plus feature folders | `{RootNamespace}.Internal` |
| `Properties/` | `AssemblyInfo.cs` and `.resx` files; assembly-level attributes live here, not in the csproj | — | — |
| `System/` | BCL-namespace extensions (`namespace System.*`) | Mirrors the BCL namespace | `System.*` — **only** in `Assimalign.Cohesion.Core` |

Precedence when a type matches more than one row: `internal` wins (an internal exception goes to
`Internal/Exceptions/`), then exceptions. One file, one category: a secondary type with a category
of its own gets its own file in its own folder; a related enum or delegate without one stays beside
its primary type.

**Public abstract classes and records are not `Abstractions/` types.** They sit with the other
public types, in the `src/` root or the feature folder they belong to, and declare the same
namespace as the types beside them (normally the `RootNamespace`; a feature folder with a namespace
of its own, such as Web.Routing's `Patterns/` and `Policies/`, keeps it). That includes the guided abstract base of an interface (see
"Interface-first with a guided abstract base"): the interface goes in `Abstractions/`, the base
does not. An abstract exception root still goes to `Exceptions/`, and an internal abstract class to
`Internal/`.

**Error codes over exception sprawl.** Prefer one `abstract` exception root per library plus a
`{Name}ErrorCode` enum in `Exceptions/` over a separate exception type per failure; consumers can
still derive their own exception types from the root.

Compiler polyfills such as `IsExternalInit` keep `System.Runtime.CompilerServices` because the
compiler binds them by exact name, marked with a `// Deviates from ...` comment. Build-generated
resource accessors live under `obj/` and use `RootNamespace`, matching their resource manifest names.

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

using Assimalign.Cohesion.Caching;
using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database;
```

**Never use global usings or `<Using Include="..." />` items in project files. Add explicit `using` directives in each file.**

## Access modifiers

1. **Implementation classes:** `internal` by default. An implementation that consumers construct
   directly is `public sealed`, with a private constructor and static `Create` methods when
   creation takes arguments. Parameterless creation uses a public parameterless constructor and
   no `Create()` (owner decision 61, 2026-10-15). It is never
   an internal type behind a static factory class (see "Creation-only facades become public
   implementations" below; owner decision 59, #1465).
   ```csharp
   internal class DatabaseConnectionPool { }
   ```
2. **Public APIs use interfaces.**
   ```csharp
   public interface ICertificateManager { }
   internal sealed class CertificateManager : ICertificateManager { }
   ```
   Exception: `resources/Database/**` is concrete-first and follows `database-area.md`
   instead (owner decision 2026-10-04).
3. **Extension containers:** always `public static`, with members inside `extension(...)`.
4. **Nested types:** match outer type visibility unless explicitly different. Debugger views
   required by "Concrete types use a private nested debugger view only to filter nonpublic state"
   are always private.
5. **Before introducing a new abstraction, check whether one already exists** in the same service root or shared library. Placeholder folders and placeholder projects are not final architecture boundaries — add projects when needed to preserve modularity and clean dependency flow.

### Creation-only facades become public implementations

A static utility class must not exist only to construct a hidden implementation. Make that
implementation `public sealed`, keep its constructor private (internal only when its own assembly
constructs it elsewhere), and move the creation methods onto it. Keep their parameters and
validation, return the concrete type, and delete the static class. Interfaces remain the contracts
consumers can depend on; this is the approved exception to the default internal implementation rule
(owner decision 59, #1464 and #1465). When creation takes no arguments, expose a public
parameterless constructor and no `Create()` instead (owner decision 61, 2026-10-15). Creation
methods that take arguments and validate them keep the decision-59 shape.

The implementation takes the static class's name when that name describes the object returned
(`AuthenticationService`, `ResourceControlPlane`, `DataProtectionProvider`). Otherwise keep its
descriptive name (`EcdsaJsonWebTokenSignatureVerifier`, `CookieAuthenticationHandler`) and use
`Create(...)` for creation with arguments.
When a facade creates different implementations, each implementation owns its creation method.
Use `git mv` when promoting or renaming the implementation, and document every public member,
including inherited contracts and their argument validation.

A facade or builder DSL that composes operations is not this pattern: `RoutePatternFactory`,
`ResourceControlPlaneMiddleware`, `ResourceRuntime` and `ResourceTelemetry` stay as they are.
Component-integration factories whose required value arguments cannot be expressed by a builder
remain governed by [component-integration.md](component-integration.md).

Database adoption is tracked by [#1470](https://github.com/assimalign/cohesion/issues/1470).
No `resources/Database/` file changes during the owner's active Database PR. The Web.Health
`HealthChecks.CreateBuilder()` conversion is deferred because Database.Hosting calls it;
`ResourceControlPlane.Create` and `JsonWebTokenWriter.CreateEs256` keep their existing spelling.

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

A **fourth** pattern exists and is deliberately not counted above: two projects grant to
*another project's* test assembly (`SecretStore.Hosting` → `SecretStore.Client.Tests` and
`ApplicationModel.Gateway` → `ApplicationModel.Gateway.InProcess.Tests`; the `IdentityHub.Hosting`
and `Rezolvr.Hosting` grants left with their retired client packages). Those are test-only
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
  <CohesionSharedSource Include="Assimalign.Cohesion.IdentityModel" Module="Materialization" />
</ItemGroup>
```

- It resolves **by project name**, exactly like `CohesionProjectReference`, off the same
  `libraries/` + `resources/` index (`build/Targets/Build.SharedFiles.targets`). No relative paths.
- `Module` is required and names one subfolder of `shared/`. Only `shared/<Module>/**/*.cs` is
  compiled, linked under `shared/<Module>/`; sibling modules and root files are excluded. Declare
  multiple items to select multiple modules from one project. Paths and wildcards are rejected.
- The item lives in the **consuming csproj**, never in a `Directory.Build.targets`. Someone
  reading the csproj has to be able to see what the assembly is built from; an allowlist one
  directory up is exactly the thing that makes linked source hard to find. This applies to the
  owning project too: `shared/` is outside the csproj's `src/` directory, so a project that wants
  its own shared folder compiled in names **itself**.
- An unknown project, missing module, invalid module name, or nonexistent module fails the build
  with `COHSHR001` through `COHSHR004`. Core's `ComponentIntegration` module is an explicitly
  opted-in internal build contract; it is not part of Core's public assembly API.

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
public interface IMultiplexedConnection : IConnection
{
    ValueTask<IMultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default);
}

public abstract class MultiplexedConnection : Connection, IMultiplexedConnection
{
    public abstract ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default);

    async ValueTask<IMultiplexedConnectionStream> IMultiplexedConnection.AcceptStreamAsync(CancellationToken cancellationToken)
        => await AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class QuicMultiplexedConnection : MultiplexedConnection { /* ... */ }
```

The interface remains the canonical public surface; concrete types derive from the base and stay `internal` where possible. Use the explicit-implementation forwarding only where the base can offer a richer, concrete-typed member; members without a richer counterpart are declared `public`/`protected abstract` directly.

**Exception: Database engines are the reverse.** In `resources/Database/**` the abstract base is the canonical public surface, no interface ships beside it, and leaves are sealed. A base exists only where real variants exist, where Hosting must stay model-agnostic, or where a lower assembly drives a seam that a higher assembly or the application implements. See `database-area.md` (owner decision 2026-10-04). Every other area keeps this rule.

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
4. **Avoid `ThrowHelper` patterns** — direct throws or static members on the exception type;
   extension members only for exception types the assembly does not own.

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
