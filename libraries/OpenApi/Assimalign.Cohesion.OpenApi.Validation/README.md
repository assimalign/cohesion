# Assimalign.Cohesion.OpenApi.Validation

Layered validation for the Cohesion OpenApi document model. Produces actionable diagnostics (severity,
machine code, JSON-pointer location) from a composable rule pipeline that runs structural, version-
placement, and semantic checks.

```csharp
OpenApiValidationResult result = document.Validate();
if (!result.IsValid)
{
    foreach (var diagnostic in result.Errors)
    {
        Console.WriteLine(diagnostic); // [Error] OPENAPI3001 at #/paths/~1pets/get/operationId: ...
    }
}
```

The official-schema conformance stage is exposed as a pluggable rule extension point. NativeAOT- and
trimming-safe. See `docs/DESIGN.md` for the diagnostics model and the rule catalogue.
