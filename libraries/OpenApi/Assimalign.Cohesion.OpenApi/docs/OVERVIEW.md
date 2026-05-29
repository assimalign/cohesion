# Assimalign.Cohesion.OpenApi — Overview

## Purpose

The canonical, version-aware object model for OpenAPI descriptions across the officially published
3.0.4, 3.1.2, and 3.2.0 lines. This is the foundation package every other OpenApi package builds on.

## Scope

- The full OpenAPI element graph: `OpenApiDocument`, `OpenApiInfo`, `OpenApiPaths`, `OpenApiOperation`,
  `OpenApiParameter`, `OpenApiRequestBody`, `OpenApiResponse`, `OpenApiComponents`, `OpenApiSchema`,
  `OpenApiSecurityScheme`, and the rest of the description surface.
- `OpenApiSpecVersion` and the `OpenApiVersionCapabilities` matrix that declares which fields and
  semantics are valid for each line.
- The format-agnostic `OpenApiNode` value tree used for examples, defaults, enums, and `x-` extensions.

## Dependencies

None outside the .NET base class library. No serialization-format or service-runtime dependencies.

## Usage

```csharp
var document = new OpenApiDocument
{
    SpecVersion = OpenApiSpecVersion.V3_1,
    Info = new OpenApiInfo { Title = "Pets API", Version = "1.0.0" }
};

var pet = new OpenApiSchema { Type = SchemaType.Object };
pet.Properties["name"] = new OpenApiSchema { Type = SchemaType.String };
pet.Required.Add("name");

document.Components = new OpenApiComponents();
document.Components.Schemas["Pet"] = pet;
```

Serialization (`Assimalign.Cohesion.OpenApi.Serialization`) turns this graph into JSON; validation
(`Assimalign.Cohesion.OpenApi.Validation`) checks it. See `docs/DESIGN.md` for the version model, the
capability matrix, and the suggested project family.
