---
paths:
  - "**/*.cs"
  - "**/*.csproj"
  - "**/*.props"
  - "**/*.targets"
---

# Pre-Completion Checklist

Run through this before declaring any non-trivial change complete. The checklist is the structural safeguard against drift — small changes are the most common drift source because they feel too minor to verify.

Mark each applicable item ✅ or ❌. If anything is ❌, fix it before reporting completion, not after.

## Build configuration

- [ ] No duplicated `<PropertyGroup>` or `<ItemGroup>` blocks across sibling csprojs that should live in shared `.props`/`.targets`
- [ ] Each `<ItemGroup>` contains only one item element type; like items are grouped together while preserving conditions and evaluation order (`build-system.md`)
- [ ] Internal dependencies use `CohesionProjectReference`, not `ProjectReference` with relative paths
- [ ] Package dependencies use `CohesionPackageReference`, not raw `PackageReference`
- [ ] Any new package version is added to `build/Targets/Build.References.Packages.targets` first
- [ ] No new references to any `Microsoft.Extensions.*` package
- [ ] No per-project `<Version>` overrides — `$(CohesionVersion)` chain intact
- [ ] No hardcoded versions on `<Import Sdk>` elements
- [ ] `<IsAotCompatible>true</IsAotCompatible>` not removed or weakened (`analyzers/` is the sanctioned exception)
- [ ] No new reflection-based serialization, runtime code generation, or `Assembly.LoadFrom()`; runtime type inspection goes through source generators
- [ ] If a new SDK or framework was added, its member list (the new Runtime producer's `Directory.Build.props`, imported by the Refs producer's) and the `KnownFrameworkReference` registration in the base SDK were both written
- [ ] `FrameworkList.xml` / `RuntimeList.xml` were not hand-edited

## Code surface

- [ ] All new files use file-scoped namespaces
- [ ] The csproj declares `<RootNamespace>` (COHNS001); namespaces match it — `{RootNamespace}.Internal` for internal types, never `.Abstractions`/`.Exceptions`/`.Extensions`/`.ValueObjects`
- [ ] Types sit in the right folder (`general-rules.md`, "Library folder structure"): only public interfaces in `Abstractions/` (public abstract classes sit with the other public types, in the `src/` root or a feature folder), exceptions and `{Name}ErrorCode` enums in `Exceptions/`, extension containers in `Extensions/`, value objects in `ValueObjects/`, every internal type under `Internal/`; `Abstractions/`, `Exceptions/`, `Extensions/`, and `ValueObjects/` stay flat
- [ ] No primary constructors on classes or structs (positional records are fine)
- [ ] Every private field — instance or static — is `_camelCase`
- [ ] Assembly attributes, `InternalsVisibleTo` included, live in `Properties/AssemblyInfo.cs`, not the csproj
- [ ] One public type per file (with grouped root-first naming for variant families, e.g., `Http2Frame.Header.cs`)
- [ ] Concrete instance types in `libraries/**/src/**/*.cs` or `resources/**/src/**/*.cs` declare a `DebuggerTypeProxy` and private nested `DebugView` only when private/internal instance fields or properties need filtering and no base class already supplies both effective debugger attributes. Reuse inherited `DebuggerDisplay` and `DebuggerTypeProxy` from abstract, concrete, or framework bases instead of redeclaring them or adding a view. All `EventSource` and `System.Exception` subclasses are excluded; preserve their preexisting debugger attributes/views. Compiler-generated backing fields, static/constant members, already hidden members, and debugger infrastructure do not alone qualify. Types needing their own view also have `DebuggerDisplay`; the view is last in the same file and projects only public/protected state through public read-only properties. Preserve existing summaries and redaction. `resources/Database/**`, build output, and everything outside the scoped `src/` folders are excluded (`general-rules.md`, "Concrete types use a private nested debugger view only to filter nonpublic state")
- [ ] New public APIs are interfaces. Implementations are internal unless consumers construct them directly; then they are `public sealed` with a private constructor and static `Create` methods for creation with arguments, or a public parameterless constructor and no `Create()` for parameterless creation (owner decision 61, 2026-10-15; `general-rules.md`, "Creation-only facades become public implementations"). Any other departure follows the exception protocol in `deviations.md`; `resources/Database/**` is concrete-first and follows `database-area.md` instead
- [ ] No static class exists only to create an object: its creation methods live on the public implementation type
- [ ] Public APIs have complete XML documentation (`<summary>`, `<param>`, `<returns>`, `<exception>`)
- [ ] Internal types are `internal`, not `public`
- [ ] No new `InternalsVisibleTo` between two shipped libraries — tests only (see `general-rules.md`)
- [ ] Any source shared between assemblies lives in the owning project's `shared/` folder, is pulled in by a `CohesionSharedSource` item **in each consuming csproj**, and passes the link-safety test — stateless statics only, or types whose instances never cross an assembly boundary (no static mutable state, singletons, `EventSource`, locks, or id generators). See `general-rules.md`
- [ ] A library reports on itself only through its internal `EventSource`, never through `ILogger`. Any new or changed `EventSource` follows `event-source.md`: `internal sealed`, one per assembly, named exactly for its assembly, no public forwarder or trace-code type, its name/manifest/lifecycle tests present, and a row in `docs/EVENT_SOURCES.md`
- [ ] No global usings or `<Using Include="..." />` items in csproj files
- [ ] Using directives ordered: System, third-party, Cohesion, blank line before code
- [ ] Code follows the existing patterns established in its category/area

## Extension members

- [ ] All new extension members use the `extension(...)` syntax
- [ ] No new uses of the legacy `this T param` extension form
- [ ] Extension containers are `public static class` with `Extensions` suffix

## Exception handling

- [ ] No new `ThrowHelper` / `ThrowHelpers` types introduced
- [ ] Reusable throw logic, if any, is a static member of the exception type it throws. Extension members only for exception types the assembly does not own (`general-rules.md`, "Direct throws or exception-type members")
- [ ] Exception messages come from `src/Properties/ErrorMessages.resx` through the build-generated `ErrorMessages` class: no literal or interpolated message strings in a `throw` or an exception constructor (`general-rules.md`, "Exception messages are build-generated resources"; Database adopts this through #1470)
- [ ] A single-valued input with a validity rule at a public boundary is a `<CohesionValueType>` value object with `IsValid`. Nothing is constructed only to make it throw, the boundary still refuses `default`, and numeric ranges use `ArgumentOutOfRangeException` guards instead (`general-rules.md`, "Validate single-valued input with generated value objects")
- [ ] Each resource name is a short name for the failure (`CookieNameInvalid`), never the message text or a truncation of it
- [ ] An exception created on a normal path, such as an abort or cancellation reason, reads its message from a cached `static readonly` field, not on each creation
- [ ] Exception roots stay scoped to the owning library or area (no new framework-wide `CohesionException`-style ancestry)
- [ ] No bare `catch (Exception)` without a justification
- [ ] No `throw ex;` (always `throw;` to preserve stack trace)

## Async

- [ ] All async methods have `Async` suffix
- [ ] All async methods accept `CancellationToken cancellationToken = default`
- [ ] No `async void` methods (event handlers excepted)
- [ ] `ValueTask<T>` considered for frequently-called methods with sync-fast-path

## Testing

- [ ] Test class named `{Feature}Tests`
- [ ] Test methods named `{Method}_{Scenario}_{ExpectedBehavior}`
- [ ] Tests follow AAA pattern (Arrange / Act / Assert)
- [ ] Assertions use Shouldly — not FluentAssertions (v8+ commercial license; removed from the repo) and not traditional `Assert.*`
- [ ] Test fixtures live under `tests/TestObjects/`

## Documentation

- [ ] Markdown files use UPPERCASE names (`README.md`, `OVERVIEW.md`, `DESIGN.md`) — except tool-fixed names (`.github/pull_request_template.md`, files under `.claude/**`)
- [ ] If a new project was added, `docs/OVERVIEW.md` and `docs/DESIGN.md` were created
- [ ] If a new area was added, an area `README.md` was created
- [ ] Every library touched has a `docs/DESIGN.md` — if one was missing, it was created in this change (canonical example: `libraries/Dns/Assimalign.Cohesion.Dns/docs/DESIGN.md`)
- [ ] If this change altered or extended a design decision (lifecycle, error model, contract shape, family layout, AOT posture, non-goals), `docs/DESIGN.md` was updated in the same commit
- [ ] Public API additions carry XML documentation in source; reference pages are maintained in `cohesion-docs`
- [ ] A newly created `docs/DESIGN.md` carries at least one `mermaid` diagram (family/dependency direction, lifecycle, protocol exchange, or pipeline); a new area `README.md` carries the area project map — see `documentation.md`
- [ ] Existing diagrams in touched docs still match what they depict; arrows still read as "references", and no `%%{init}%%` theming or hardcoded `fill:`/`color:` was introduced
- [ ] Any mermaid block added or edited was render-previewed — parse errors show up on GitHub and nothing in CI catches them

## Deviations from rules

- [ ] Any user-directed deviation from a rule is documented with a `// Deviates from the repo <rule> rule ...` comment at the relevant entry point (see `deviations.md`)
- [ ] The deviation is scoped to the specific area requested, not generalized
- [ ] The deviation is mentioned in the change summary

## Sanity build

- [ ] If the change touches MSBuild or SDK plumbing, a local pack via `pwsh scripts/Install-Local.ps1` was at least considered (or run if the change is non-trivial)
- [ ] If the change touches a specific library, `dotnet build` and `dotnet test` were considered for that project

---

If something isn't applicable (e.g., the change is documentation-only and has no MSBuild touches), mark it N/A and move on. The goal is to actively verify each applicable item, not to mechanically check every box.
