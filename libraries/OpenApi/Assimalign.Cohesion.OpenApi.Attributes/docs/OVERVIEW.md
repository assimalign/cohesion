# Assimalign.Cohesion.OpenApi.Attributes — Overview

An attribute model for describing OpenApi metadata in application code, plus the flat intermediate
metadata the attributes map to and a mapper that applies the mapping rules with diagnostics.

## Scope

- Attributes: `[OpenApiOperation]`, `[OpenApiParameter]`, `[OpenApiRequestBody]`, `[OpenApiResponse]`,
  `[OpenApiSchema]`, `[OpenApiSchemaProperty]`, `[OpenApiExample]`, `[OpenApiTag]`,
  `[OpenApiSecurityScheme]`, `[OpenApiSecurityRequirement]`.
- Metadata: flat `OpenApi*Metadata` records the source generator emits and the generation pipeline
  consumes.
- `OpenApiAttributeMapper`: maps attribute instances to metadata, reporting invalid combinations.

## Dependencies

- `Assimalign.Cohesion.OpenApi` (for `OperationType`, `ParameterLocation`, `SchemaType`,
  `SecuritySchemeType`). No serialization or validation dependency.

## Usage

See [AUTHORING.md](./AUTHORING.md) for the full guide. In brief:

```csharp
[OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getPet")]
[OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer)]
[OpenApiResponse(200, Description = "The pet", ModelType = typeof(Pet))]
public static void GetPet() { }
```

See [DESIGN.md](./DESIGN.md) for the three-layer design and AOT posture.
