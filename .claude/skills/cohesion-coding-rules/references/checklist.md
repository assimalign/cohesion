# Pre-Completion Checklist

Run through this before declaring any non-trivial change complete. The checklist is the structural safeguard against drift — small changes are the most common drift source because they feel too minor to verify.

Mark each applicable item ✅ or ❌. If anything is ❌, fix it before reporting completion, not after.

## Build configuration

- [ ] No duplicated `<PropertyGroup>` or `<ItemGroup>` blocks across sibling csprojs that should live in shared `.props`/`.targets`
- [ ] Internal dependencies use `CohesionProjectReference`, not `ProjectReference` with relative paths
- [ ] Package dependencies use `CohesionPackageReference`, not raw `PackageReference`
- [ ] Any new package version is added to `build/Targets/PackageReferences.targets` first
- [ ] No new references to any `Microsoft.Extensions.*` package
- [ ] No per-project `<Version>` overrides — `$(CohesionVersion)` chain intact
- [ ] No hardcoded versions on `<Import Sdk>` elements
- [ ] `<IsAotCompatible>true</IsAotCompatible>` not removed or weakened
- [ ] If a new SDK or framework was added, the manifest in `frameworks/Assimalign.Cohesion.App.props` and the `KnownFrameworkReference` registration in the base SDK were both updated
- [ ] `FrameworkList.xml` / `RuntimeList.xml` were not hand-edited

## Code surface

- [ ] All new files use file-scoped namespaces
- [ ] Namespace matches assembly name exactly
- [ ] One public type per file (with grouped root-first naming for variant families, e.g., `Http2Frame.Header.cs`)
- [ ] New public APIs are interfaces, with internal implementations (unless a documented deviation applies — see SKILL.md exception protocol)
- [ ] Public APIs have complete XML documentation (`<summary>`, `<param>`, `<returns>`, `<exception>`)
- [ ] Internal types are `internal`, not `public`
- [ ] No global usings or `<Using Include="..." />` items in csproj files
- [ ] Using directives ordered: System, third-party, Cohesion, blank line before code

## Extension members

- [ ] All new extension members use the `extension(...)` syntax
- [ ] No new uses of the legacy `this T param` extension form
- [ ] Extension containers are `public static class` with `Extensions` suffix

## Exception handling

- [ ] No new `ThrowHelper` / `ThrowHelpers` types introduced
- [ ] Reusable throw logic, if any, is implemented as a .NET 10 extension type method in `Extensions/`
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
- [ ] Assertions use Shouldly or FluentAssertions, not traditional `Assert.*`
- [ ] Test fixtures live under `tests/TestObjects/`

## Documentation

- [ ] Markdown files use UPPERCASE names (`README.md`, `OVERVIEW.md`, `DESIGN.md`) — except `docs/Assembly/` API reference files
- [ ] If a new project was added, `docs/OVERVIEW.md` and `docs/DESIGN.md` were created
- [ ] If a new area was added, an area `README.md` was created
- [ ] Every library touched has a `docs/DESIGN.md` — if one was missing, it was created in this change (canonical example: `libraries/Dns/Assimalign.Cohesion.Dns/docs/DESIGN.md`)
- [ ] If this change altered or extended a design decision (lifecycle, error model, contract shape, family layout, AOT posture, non-goals), `docs/DESIGN.md` was updated in the same commit
- [ ] Public API additions are reflected in `docs/Assembly/<Namespace>/<Type>.md` if that file exists for the type

## Deviations from rules

- [ ] Any user-directed deviation from a rule is documented with a `// Deviates from AGENTS.md ...` comment at the relevant entry point
- [ ] The deviation is scoped to the specific area requested, not generalized
- [ ] The deviation is mentioned in the change summary

## Sanity build

- [ ] If the change touches MSBuild or SDK plumbing, a local pack via `pwsh installer/scripts/Install-Local.ps1` was at least considered (or run if the change is non-trivial)
- [ ] If the change touches a specific library, `dotnet build` and `dotnet test` were considered for that project

---

If something isn't applicable (e.g., the change is documentation-only and has no MSBuild touches), mark it N/A and move on. The goal is to actively verify each applicable item, not to mechanically check every box.
