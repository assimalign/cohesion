# Assimalign.Cohesion.OpenApi.Generation — Overview

The OpenApi document generation pipeline. `OpenApiDocumentGenerator.Generate(input, options)` turns the
flat intermediate metadata — from the source generator's `OpenApiMetadataRegistry` or the runtime
attribute mapper — into a version-targeted `OpenApiDocument`.

## Scope

- `OpenApiGenerationInput` — the collected metadata (operations, schemas, tags, security schemes).
- `OpenApiGenerationOptions` — the target OpenAPI line and required document metadata.
- `OpenApiDocumentGenerator` — assembles the version-clean model.

## Dependencies

- `Assimalign.Cohesion.OpenApi` (model) and `Assimalign.Cohesion.OpenApi.Attributes` (metadata types).
- The source generator that emits `OpenApiMetadataRegistry` ships inside the Attributes package, so a
  project that references this package gets it through that dependency. A project inside this
  repository adds `<CohesionAnalyzerReference Include="Assimalign.Cohesion.OpenApi.SourceGeneration" />`,
  because a project reference carries no analyzer.
- The registry is internal to each assembly and combines that assembly's metadata with every annotated
  assembly it references, so one input describes an application whose endpoints span several libraries.

## Usage

```csharp
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Generation;
using Assimalign.Cohesion.OpenApi.Generated; // this assembly's generated registry, internal

var input = new OpenApiGenerationInput
{
    Operations = OpenApiMetadataRegistry.Operations,
    Schemas = OpenApiMetadataRegistry.Schemas,
    Tags = OpenApiMetadataRegistry.Tags,
    SecuritySchemes = OpenApiMetadataRegistry.SecuritySchemes
};

var document = OpenApiDocumentGenerator.Generate(
    input,
    new OpenApiGenerationOptions { Version = OpenApiSpecVersion.V3_1, Title = "Petstore", ApiVersion = "1.0.0" });
```

See [DESIGN.md](./DESIGN.md) for the version-targeting model and AOT posture.
