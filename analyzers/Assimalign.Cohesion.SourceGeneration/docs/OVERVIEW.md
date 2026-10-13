# Assimalign.Cohesion.SourceGeneration

The root source-generator project serves contracts owned by `libraries/Core`, following owner
decision 59 of 2026-10-13. It provides `ComponentIntegrationGenerator`, which reads Core's
`ComponentIntegrationAttribute` declarations and emits registration verbs when a consuming
compilation references the named composition seam.

Core packages include this analyzer, and the App targeting pack carries it for SDK consumers.
In-repository projects that call generated verbs reference it with:

```xml
<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration" />
```

The analyzer targets `netstandard2.0` with `IsAotCompatible=false` because Roslyn loads it inside the
compiler. It has no runtime-library dependency; its references are Roslyn tooling packages.
Its tests live in `Assimalign.Cohesion.SourceGeneration.Tests` and exercise generated integrations
through consuming compilations. Diagnostics keep ids `COHCMP0001` through `COHCMP0007` and are tracked
in the project's `AnalyzerReleases` files.

Area-owned generators live in `Assimalign.Cohesion.SourceGeneration.<Area>`. See [DESIGN.md](DESIGN.md)
for the integration model, diagnostics, generated code, and delivery constraints.
