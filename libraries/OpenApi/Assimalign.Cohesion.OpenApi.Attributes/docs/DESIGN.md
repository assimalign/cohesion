# Assimalign.Cohesion.OpenApi.Attributes — Design

## Design intent

A declarative attribute surface for describing OpenAPI metadata in application code, plus the flat
**intermediate metadata** those attributes map to. The attributes are the authoring surface; the
metadata is the contract the AOT-safe source generator emits and the generation pipeline consumes. The
attributes deliberately target this neutral metadata layer rather than binding to any hosting stack, so
Web, ApiManager, or any other consumer can adopt them without dragging in unrelated runtime concerns.

## Three layers

```
[OpenApiOperation] …attributes…   →   OpenApiOperationMetadata …descriptors…   →   canonical model
        (authoring)                          (intermediate)                        (generation, F6)
```

1. **Attributes** (`src/Attributes/`) — the compile-time authoring surface: operation, parameter,
   request body, response, schema, schema property, example, tag, security scheme, and security
   requirement. They are pure data carriers with no behavior.
2. **Metadata** (`src/Metadata/`) — flat, immutable `required`/`init` records
   (`OpenApiOperationMetadata`, `OpenApiSchemaMetadata`, …). Deliberately simpler than the rich model:
   references are strings, types are enums, nothing nests a model object. This is what a source
   generator can emit as plain object initializers.
3. **Mapper** (`OpenApiAttributeMapper`) — maps attribute instances to metadata, applying the rules and
   reporting invalid combinations as `OpenApiMetadataDiagnostic` values.

## Why a separate metadata layer (not map straight to the model)

The generation pipeline (feature .06) turns metadata into the canonical `OpenApiDocument`. Keeping an
intermediate representation means the *discovery* side (attributes → metadata, whether by the runtime
mapper or the source generator over Roslyn symbols) and the *emission* side (metadata → model) evolve
independently, and the source generator emits simple data rather than reconstructing the full model
graph in generated code. The metadata is the stable seam #542 asks to "lock down before the source
generator depends on it."

## AOT and source-generator friendliness

- **No `Type` reflection.** The mapper reads only the attribute values it is handed and, for a body's
  `ModelType`, its `Type.Name` — never its members. Schema references are derived by the
  `#/components/schemas/{TypeName}` convention, which the generator reproduces from a symbol name.
- **No nullable-enum attribute arguments.** A nullable enum is not a valid attribute argument type, so
  the optional scalar type on parameter/property attributes uses `OpenApiSchemaKind` (with an
  `Unspecified` sentinel); the mapper converts it to `SchemaType?`.
- **The runtime mapper and the generator share rule identity.** Both use the
  `OpenApiMetadataDiagnosticCodes`, so a diagnostic raised at run time matches the compiler diagnostic
  the generator would raise for the same shape.

## Mapping rules and diagnostics

The mapper corrects or flags these combinations (codes in `OpenApiMetadataDiagnosticCodes`):

| Situation | Code | Severity | Behavior |
|---|---|---|---|
| Operation with an empty path | `OPENAPIATTR0001` | Error | Reported |
| Body with both `ModelType` and `SchemaReference` | `OPENAPIATTR0002` | Error | The explicit reference wins |
| Path parameter not marked required | `OPENAPIATTR0003` | Warning | Corrected to required |
| Example with both embedded and external value | `OPENAPIATTR0004` | Error | Reported |
| Example with neither value | `OPENAPIATTR0005` | Warning | Reported |
| API key scheme missing name/location | `OPENAPIATTR0006` | Error | Reported |

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). The mapper performs no member reflection; the
only reflection in the test suite (reading attributes off a sample type) is test-only. Runtime
discovery in an application is the source generator's job (feature .06).

## Non-goals

- Runtime reflection-based discovery of attributes across an assembly — that is deliberately the source
  generator's responsibility; a reflection fallback would break the AOT posture.
- Emitting the canonical model — that is the generation pipeline (feature .06). This library stops at
  the metadata.
- Covering every model field via attributes; rarely authored corners are reached by dropping to the
  fluent or model layers.
