---
paths: ["analyzers/**"]
---

# Analyzers, generators and code fixes

## Placement follows the source

Owner decision 59 (2026-10-13, `docs/programs/HTTP_WEB_PROGRAM_PLAN.md` §7.4): an
analyzer, generator or code fix lives where the source it serves lives.

- `libraries/Core` uses `Assimalign.Cohesion.SourceGeneration` or
  `Assimalign.Cohesion.CodeFixes`.
- `libraries/<Area>` and `resources/<Area>` use
  `Assimalign.Cohesion.SourceGeneration.<Area>` or
  `Assimalign.Cohesion.CodeFixes.<Area>`.

Component integration belongs to the root generator because its attribute lives in Core.
ObjectMapping and OpenApi belong to their area generators. Each project owns the internal
helpers it needs; a merged project keeps one copy of each shared helper.

## Project and test names

Each project lives under `analyzers/<Project>/`, with `src/`, `tests/` and `docs/` siblings.
Its source project is `src/<Project>.csproj`; its assembly name and explicit `RootNamespace`
match `<Project>`. Namespaces follow the root, with internal helpers under `.Internal`.
Compiler polyfills retain the namespace the compiler requires.

The test project is `tests/<Project>.Tests.csproj`, with assembly name and explicit
`RootNamespace` `<Project>.Tests`. Update solutions, consumer analyzer references, framework
analyzers, workflow project lists and path filters when a project moves; regenerate
`docs/DEPENDENCIES.md`.

## Build-time exception

Roslyn components target `netstandard2.0` with `IsAotCompatible=false`, inherited from
`analyzers/Directory.Build.props`. This is the sanctioned exception to repository runtime
TFM/AOT defaults: the compiler loads the tooling assembly. Generated code must preserve
NativeAOT and trimming compatibility. Test hosts use `$(TargetFrameworkLatest)` and are not
Roslyn components; their shared defaults also live in `analyzers/Directory.Build.props`.

## Stable diagnostics and generated APIs

A move or rename preserves every diagnostic id, severity and meaning, and preserves generated
APIs and behavior. Diagnostic ids belong to their existing diagnostic family, not the current
project spelling. Track diagnostics in `src/AnalyzerReleases.Shipped.md` and
`src/AnalyzerReleases.Unshipped.md`, included as `AdditionalFiles` in the source project.
Move release entries with their diagnostics and merge them without duplicate ids when projects
combine. A project with no diagnostics needs no empty release ledger. Do not reset shipped
history because a project or assembly has a new name.
