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

## Usage

```csharp
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Generation;
using Assimalign.Cohesion.OpenApi.Generated; // emitted by the source generator

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
