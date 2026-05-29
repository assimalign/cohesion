# Assimalign.Cohesion.OpenApi.Validation — Overview

## Purpose

Validates Cohesion OpenApi documents and reports diagnostics that are useful to both tooling and runtime
integrations.

## Scope

- `OpenApiDiagnostic` / `OpenApiDiagnosticSeverity` / `OpenApiValidationResult` — the diagnostics model.
- `IOpenApiValidator`, `IOpenApiValidationRule`, `OpenApiValidationContext` — the validator and rule
  contracts.
- `OpenApiValidation` — static entry points (`Validate`, `CreateDefault`, `Create`, `DefaultRules`).
- `OpenApiValidationRuleCodes` — the stable diagnostic codes the built-in rules emit.
- `document.Validate()` — ergonomic extension member.

## Dependencies

`Assimalign.Cohesion.OpenApi` (the model) only.

## Usage

```csharp
var result = document.Validate();
result.IsValid;          // false if any Error diagnostics
result.Errors;           // Error-severity diagnostics
result.Diagnostics;      // everything, in report order

// Compose a custom pipeline (e.g. add an official-schema rule):
var validator = OpenApiValidation.Create([.. OpenApiValidation.DefaultRules(), new MySchemaRule()]);
```

See `docs/DESIGN.md` for the rule catalogue and the official-schema extension point.
