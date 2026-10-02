# Assimalign.Cohesion.OpenApi.SourceGeneration

A Roslyn incremental source generator that discovers the OpenApi authoring attributes at compile time
and emits an `OpenApiMetadataRegistry` carrying the flat intermediate metadata, so document generation
needs no runtime reflection. Invalid attribute combinations are reported as compiler diagnostics whose
ids match the runtime mapper's `OpenApiMetadataDiagnosticCodes`.

It has no NuGet package of its own. It ships inside `Assimalign.Cohesion.OpenApi.Attributes`, under
`analyzers/dotnet/cs/`, so a project that references that package, directly or through
`OpenApi.Generation` or `OpenApi.Integration`, runs the generator with no extra wiring.

A project inside this repository references the OpenApi packages by project, and a project reference
does not carry an analyzer, so it activates the generator itself:

```xml
<ItemGroup>
  <CohesionAnalyzerReference Include="Assimalign.Cohesion.OpenApi.SourceGeneration" />
</ItemGroup>
```

Then read the generated metadata (namespace `Assimalign.Cohesion.OpenApi.Generated`) and feed it to
`Assimalign.Cohesion.OpenApi.Generation.OpenApiDocumentGenerator`.

- [docs/DESIGN.md](./docs/DESIGN.md) — the generator's pipeline, emitted shape, caching design, and
  delivery
