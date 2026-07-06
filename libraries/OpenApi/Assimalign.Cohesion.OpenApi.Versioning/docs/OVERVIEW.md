# Assimalign.Cohesion.OpenApi.Versioning — Overview

Version targeting and transforms for the OpenApi model. `OpenApiVersionTransformer.Transform` (or the
`document.TransformTo(target)` extension) converts a document between OpenAPI 3.0.4, 3.1.2, and 3.2.0,
applying the documented construct changes and returning a diagnostic for every conversion and every
construct that cannot translate.

## Scope

- `OpenApiVersionTransformer` / `TransformTo` — the transform entry point.
- `OpenApiTransformResult` — the transformed document plus findings.
- `OpenApiTransformDiagnostic` / `OpenApiTransformDiagnosticCodes` — the findings model.

## Dependencies

- `Assimalign.Cohesion.OpenApi` (model), `Assimalign.Cohesion.OpenApi.Serialization` (deep copy),
  `Assimalign.Cohesion.OpenApi.Validation` (version-fit analysis).

## Usage

```csharp
using Assimalign.Cohesion.OpenApi.Versioning;

var result = document.TransformTo(OpenApiSpecVersion.V3_1);
foreach (var diagnostic in result.Diagnostics)
{
    Console.WriteLine(diagnostic); // [Warning] OPENAPIVER0006 (#/webhooks): ...
}

var upgraded = result.Document; // targets 3.1, serializes and validates cleanly
```

See [DESIGN.md](./DESIGN.md) for the conversion rules and the shared capability contract.
