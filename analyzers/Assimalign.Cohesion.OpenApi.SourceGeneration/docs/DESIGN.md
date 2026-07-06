# Assimalign.Cohesion.OpenApi.SourceGeneration — Design

## Design intent

The AOT-safe discovery half of the attribute path: find the OpenApi authoring attributes at compile
time and emit their metadata as static data, so an application never reflects over its own assembly at
run time to build an OpenAPI description. This is the reflection-free replacement for a
`Type.GetCustomAttributes` scan, and it is what makes the attribute path NativeAOT- and trimming-safe.

## Pipeline

An `IIncrementalGenerator` with three inputs combined into one emit:

- **Operations** — `ForAttributeWithMetadataName(OpenApiOperationAttribute)` over method declarations.
  The transform reads the operation attribute plus the method's parameter, request-body, response, and
  security-requirement attributes, and builds one `OpenApiOperationMetadata` initializer.
- **Schemas** — `ForAttributeWithMetadataName(OpenApiSchemaAttribute)` over type declarations, reading
  each member's `[OpenApiSchemaProperty]`.
- **Document-level** — a `CompilationProvider` scan for `[OpenApiTag]` and `[OpenApiSecurityScheme]` on
  the assembly and on types (these attributes allow assembly targets, which `ForAttributeWithMetadataName`
  does not surface, so a scan is used; there are few of them and they are cross-cutting).

The three are `Combine`d and `RegisterSourceOutput` emits a single `OpenApiMetadataRegistry` class in
the `Assimalign.Cohesion.OpenApi.Generated` namespace exposing four `IReadOnlyList` properties.

## Emitting data, not the model

The generator emits the flat **metadata** records (from `Assimalign.Cohesion.OpenApi.Attributes`) as
object initializers — not the canonical model. This keeps the generated code simple (plain data) and
defers model assembly to the generation pipeline (feature .06's `OpenApiDocumentGenerator`), which the
consumer references. `ModelType = typeof(Pet)` resolves to `#/components/schemas/Pet` by the type's
name — the same convention the runtime mapper uses — so a symbol name and a runtime `Type` produce the
same reference.

## Incremental caching

Generator pipeline models must compare by value or the incremental cache never recognizes unchanged
inputs. Two decisions enforce that:

- Each per-symbol transform returns a **string** initializer fragment (value-equatable) rather than a
  model holding `ISymbol`s.
- Diagnostics are captured as `DiagnosticInfo` — a value-equatable record holding a serializable
  location (file path + spans) rather than a `Location` — wrapped in an `EquatableArray<T>` so the
  collection compares by element.

## Diagnostics

The generator reports the same rule set the runtime mapper does, with matching ids
(`OPENAPIATTR0001`–`OPENAPIATTR0006`): empty operation path, ambiguous body schema, non-required path
parameter (a warning; the metadata is corrected), conflicting example values, and an incomplete API key
scheme. Analyzer release tracking is declared in `AnalyzerReleases.*.md`.

## Build posture

netstandard2.0, `IsRoslynComponent=true`, `IsAotCompatible=false` — inherited from
`analyzers/Directory.Build.props`. The generator is a build-time component; the AOT constraint applies
to the *generated* code (which is reflection-free static data), not to the generator assembly itself.

## Non-goals

- Emitting the canonical model — that is the generation pipeline's job; the generator stops at metadata.
- Structural schema inference — a body schema is referenced by type name, matched to an
  `[OpenApiSchema]` model; the generator does not synthesize property schemas from arbitrary CLR types.
- Discovering attributes across referenced assemblies — the generator sees the compilation it runs in.
