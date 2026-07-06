# Assimalign.Cohesion.OpenApi.SourceGeneration

A Roslyn incremental source generator that discovers the OpenApi authoring attributes at compile time
and emits an `OpenApiMetadataRegistry` carrying the flat intermediate metadata, so document generation
needs no runtime reflection. Invalid attribute combinations are reported as compiler diagnostics whose
ids match the runtime mapper's `OpenApiMetadataDiagnosticCodes`.

Consume it from a library or app with:

```xml
<ItemGroup>
  <CohesionAnalyzerReference Include="Assimalign.Cohesion.OpenApi.SourceGeneration" />
</ItemGroup>
```

Then read the generated metadata (namespace `Assimalign.Cohesion.OpenApi.Generated`) and feed it to
`Assimalign.Cohesion.OpenApi.Generation.OpenApiDocumentGenerator`.

- [docs/DESIGN.md](./docs/DESIGN.md) — the generator's pipeline, emitted shape, and caching design
