# Assimalign.Cohesion.SourceGeneration

The root source-generator project serves contracts owned by `libraries/Core`, following owner
decision 59 of 2026-10-13. It provides `ComponentIntegrationGenerator`, which reads Core's
internal `shared/ComponentIntegration` declarations and emits registration verbs when a consuming
compilation references the named composition seam. It also carries the in-process program
bindings (`InProcessProgramBridgeGenerator`, `InProcessProgramCatalogGenerator`): a resource
executable's build exposes its entry point, and an in-process gateway's build registers the
programs it references, so the gateway needs no `AddProgram` call.

Core packages include this analyzer, and the App targeting pack carries it for SDK consumers.
Contributing projects opt into the attribute and its closed enums with
`<CohesionSharedSource Include="Assimalign.Cohesion.Core" Module="ComponentIntegration" />`.
Each declaration selects one template; ordinary/provider builder callbacks have separate declarations.
Custom declarations supply a parameter list and complete C# block with documented placeholders.
In-repository projects that call generated verbs reference it with:

```xml
<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration" />
```

The analyzer targets `netstandard2.0` with `IsAotCompatible=false` because Roslyn loads it inside the
compiler. It has no runtime-library dependency; its references are Roslyn tooling packages.
Its tests live in `Assimalign.Cohesion.SourceGeneration.Tests` and exercise generated integrations
through consuming compilations. Analyzer test projects use the `<Project>.Tests` name and inherit
executable test-host defaults from `analyzers/Directory.Build.props`. Diagnostics keep ids `COHCMP0001` through `COHCMP0007` and `COHAPM0001` through `COHAPM0003`, tracked
in the project's `AnalyzerReleases` files.

Area-owned generators live in `Assimalign.Cohesion.SourceGeneration.<Area>`. See [DESIGN.md](DESIGN.md)
for the integration model, diagnostics, generated code, and delivery constraints. The repository's [analyzer rule](../../../.claude/rules/analyzers.md) governs placement, project/test naming and release tracking.
