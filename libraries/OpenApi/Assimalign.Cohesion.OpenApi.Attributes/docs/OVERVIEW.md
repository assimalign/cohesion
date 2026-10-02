# Assimalign.Cohesion.OpenApi.Attributes — Overview

An attribute model for describing OpenApi metadata in application code, plus the flat intermediate
metadata the attributes map to and a mapper that applies the mapping rules with diagnostics.

## Scope

- Attributes: `[OpenApiOperation]`, `[OpenApiParameter]`, `[OpenApiRequestBody]`, `[OpenApiResponse]`,
  `[OpenApiSchema]`, `[OpenApiSchemaProperty]`, `[OpenApiExample]`, `[OpenApiTag]`,
  `[OpenApiSecurityScheme]`, `[OpenApiSecurityRequirement]`.
- Metadata: flat `OpenApi*Metadata` records the source generator emits and the generation pipeline
  consumes.
- Provider contract: `IOpenApiMetadataProvider` and `[assembly: OpenApiMetadataProvider]`, through which
  an assembly advertises its generated metadata so a referencing compilation composes it at compile
  time.
- `OpenApiAttributeMapper`: maps attribute instances to metadata, reporting invalid combinations.

## Dependencies

- `Assimalign.Cohesion.OpenApi` (for `OperationType`, `ParameterLocation`, `SchemaType`,
  `SecuritySchemeType`). No serialization or validation dependency.
- Carries the `Assimalign.Cohesion.OpenApi.SourceGeneration` analyzer under `analyzers/dotnet/cs/`.
  It runs at build time and is not a package dependency: a project that references this package,
  directly or through `OpenApi.Generation` or `OpenApi.Integration`, gets an internal
  `OpenApiMetadataRegistry` that combines its own annotated code with every annotated assembly it
  references. A project inside this repository references the package by project, which carries no
  analyzer, so it adds
  `<CohesionAnalyzerReference Include="Assimalign.Cohesion.OpenApi.SourceGeneration" />` itself.

## Usage

See [AUTHORING.md](./AUTHORING.md) for the full guide. In brief:

```csharp
[OpenApiOperation(OperationType.Get, "/pets/{id}", OperationId = "getPet")]
[OpenApiParameter("id", ParameterLocation.Path, Required = true, SchemaType = OpenApiSchemaKind.Integer)]
[OpenApiResponse(200, Description = "The pet", ModelType = typeof(Pet))]
public static void GetPet() { }
```

See [DESIGN.md](./DESIGN.md) for the three-layer design and AOT posture.
