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
   references are strings, types are enums, nothing nests a model object except the optional
   pass-through `Schema` runtime producers set (see "Pass-through schemas"). This is what a source
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

## Pass-through schemas (#152)

The flat vocabulary is the attributes' vocabulary, and it cannot say what a serialization contract
says: an array of a component, a dictionary, an enum, a nullable reference, a nested inline object. A
producer that already holds a complete schema therefore passes it through instead of flattening it:
`OpenApiParameterMetadata`, `OpenApiRequestBodyMetadata`, `OpenApiResponseMetadata` and
`OpenApiSchemaMetadata` each have an optional `Schema` (`OpenApiSchema?`). When it is set, generation
places that model schema as it is and ignores the flat type, format, reference or property fields beside
it.

This is the one place the metadata holds a model object, and it is deliberately narrow:

- **Only runtime producers set it.** The Web OpenAPI adapter (`Assimalign.Cohesion.Web.OpenApi`) derives
  schemas from System.Text.Json contracts with `JsonSchemaExporter`. The attribute mapper and the source
  generator never set it, so their output, and the plain object initializers the generator emits, are
  unchanged.
- **It is optional and additive.** Existing producers and consumers compile and behave as before.
- **It keeps one assembler.** The schema still reaches the document through the description provider
  and the generator; the producer does not patch a generated document afterwards, which would split
  document assembly across packages (the alternative the adapter's DESIGN rejects).

A producer that passes schemas through writes them for the line it targets, since the model's writer
adapts nullability and the 3.1 vocabulary per line but cannot know a producer's intent: the Web adapter
builds its source per document line.

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

## The provider contract: metadata across assemblies

An application's annotated endpoints are usually spread over several assemblies, and each assembly's
metadata is generated in that assembly's own compile. Two public, hand-written types carry it across:

- `IOpenApiMetadataProvider` (`src/Abstractions/`) — the operations, schemas, tags, and security schemes
  one assembly contributes.
- `OpenApiMetadataProviderAttribute` (`src/Attributes/`) — `[assembly: OpenApiMetadataProvider(typeof(T))]`
  advertises a provider type to every compilation that references the assembly.

The source generator implements the interface once per annotated assembly and applies the attribute for
that implementation. In each referencing compilation it reads the advertised attributes from metadata at
compile time and emits an internal `OpenApiMetadataRegistry` that constructs every provider directly, so
nothing is discovered at run time and trimming keeps each provider. The attribute is not generator-only:
an assembly can advertise a hand-written provider the same way, under the rules in the attribute's XML
documentation.

The contract lives here, not in a package of its own, because the generator ships here: every
compilation that runs the generator references this package, so the code it emits always compiles
against the contract. Why composition has this shape, and the alternatives rejected (module
initializers, hand composition, the former fixed public registry), are recorded in the generator's
[DESIGN.md](../../../../analyzers/Assimalign.Cohesion.OpenApi.SourceGeneration/docs/DESIGN.md)
("Composing metadata across assemblies").

## The package carries the source generator

The project declares `CohesionAnalyzerReference` for `Assimalign.Cohesion.OpenApi.SourceGeneration`,
so the generator DLL ships in this package at `analyzers/dotnet/cs/` and runs in every project that
references the package, directly or through `OpenApi.Generation` or `OpenApi.Integration`. Shipping the
two together means the generator cannot version apart from what its output compiles against, the
metadata records and the provider contract: renaming or reshaping one of them is a change to the
generator's emitted code in the same package. Why this package is the carrier, and the alternatives
rejected, are recorded in the generator's
[DESIGN.md](../../../../analyzers/Assimalign.Cohesion.OpenApi.SourceGeneration/docs/DESIGN.md)
("Delivery").

## Non-goals

- Runtime reflection-based discovery of attributes across an assembly — that is deliberately the source
  generator's responsibility; a reflection fallback would break the AOT posture.
- Emitting the canonical model — that is the generation pipeline (feature .06). This library stops at
  the metadata.
- Covering every model field via attributes; rarely authored corners are reached by dropping to the
  fluent or model layers.
