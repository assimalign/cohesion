# Assimalign.Cohesion.OpenApi.Validation — Design

## Design intent

Validation runs in layers so a caller can tell apart a structural defect, a version-placement mistake,
and a semantic violation. Every finding is an `OpenApiDiagnostic` — never an exception — so tooling and
runtime integrations can surface, group, and act on them. A document is "valid" when it produces no
`Error`-severity diagnostics; `Warning`/`Info` never fail it.

## Pipeline

`IOpenApiValidator` runs an ordered list of `IOpenApiValidationRule` instances against a single
`OpenApiValidationContext` (which holds the document and accumulates diagnostics). `OpenApiValidation`
exposes the default pipeline and a `Create(rules)` factory for composing custom ones.

The built-in rules:

| Rule | Concern | Representative codes |
|---|---|---|
| `StructuralValidationRule` | Required fields, path-template shape, mutually exclusive fields, version-aware response description | `OPENAPI1001`–`OPENAPI1003` |
| `VersionPlacementRule` | Fields/features used outside the versions that support them | `OPENAPI2001` |
| `SemanticValidationRule` | operationId uniqueness, path-parameter consistency, parameter shape/uniqueness, querystring usage, response keys, security-scheme references, example value exclusivity, OAuth flow completeness, reserved additional operations, link `operationId`/`operationRef` exclusivity | `OPENAPI3001`–`OPENAPI3013` |
| `OpenApiSchemaConformanceRule` (opt-in) | Divergence from the official meta-schema for the document's line | `OPENAPI4001` |

Codes are stable and published in `OpenApiValidationRuleCodes` (`1xxx` structural, `2xxx` version,
`3xxx` semantic, `4xxx` schema-conformance) so tests and consumers can assert on them. Diagnostic
locations are JSON Pointers (RFC 6901), e.g. `#/paths/~1pets~1{id}/get/operationId`.

### Reference-aware semantic checks

Path-parameter consistency resolves a single internal `#/components/parameters/{name}` reference before
matching placeholders, so a parameter declared once in components and referenced from many paths (the
common factoring, used by the official `tictactoe` example) is recognized rather than reported missing.
An *unresolvable* reference (external, or a ref chain) suppresses the missing-parameter check for that
path — the referenced definition might supply the parameter, and a false "missing" is worse than a
missed one. This asymmetry is deliberate: the rule never reports a violation it cannot prove.

## Why the capability matrix drives version validation

`VersionPlacementRule` does not hard-code version rules; it asks `OpenApiVersionCapabilities.Supports`,
the **same** matrix the serializer uses to gate emission. This guarantees the writer and the validator
agree on what each line allows — if a field is omitted on write for a version, using it in that version
is flagged on validate, from one source of truth.

The rule covers document-level fields, servers, tags, path items (the 3.2 `query` operation and
`additionalOperations`), every operation reached through `OpenApiOperationWalker` (parameter locations
and styles, response summaries, media-type streaming fields, content-map references, example
`dataValue`/`serializedValue`), component security schemes, and the top level of component schemas
(type arrays, boolean forms, `$ref` siblings, and the 2020-12 keyword vocabulary).

## Official-schema conformance stage

`OpenApiSchemaConformanceRule` serializes the document for its declared line and evaluates the result
against the **official OpenAPI meta-schema** for that line, vendored under `src/Schemas/` (see the
[Schemas README](../src/Schemas/README.md) for provenance and licensing). It is composed in explicitly —
`OpenApiValidation.Create([.. DefaultRules(), OpenApiValidation.CreateOfficialSchemaRule()])` — and is
**not** part of the default pipeline, so callers opt into the extra cost.

Findings are `Warning`-severity (`OPENAPI4001`), never errors, because the OpenAPI Initiative publishes
the schema files as *informational*: where the schema and the specification text disagree, the
**specification text is authoritative**. A schema warning is a signal, not a verdict.

### The evaluator

`JsonSchemaEvaluator` is a purpose-built JSON Schema evaluator, not a general one. It supports exactly
the keyword set the three vendored meta-schemas use, across both dialects they span — draft-04 (the 3.0
schema) and draft 2020-12 (the 3.1/3.2 schemas): the core applicators (`allOf`/`anyOf`/`oneOf`/`not`,
`if`/`then`/`else`, `dependentSchemas`), object/array/string/number constraints (including
`contains`/`min`/`maxContains`, `patternProperties`, `propertyNames`, `unevaluatedProperties` with
annotation tracking), `$ref`/`$defs`/`definitions`, and `$dynamicRef` resolved to the sole `meta`
dynamic anchor (sufficient because the base OAS schemas fix their dynamic scope at the root).

The critical correctness property: **an omitted keyword becomes a vacuous pass, which under `not` or
`contains` flips into a false positive.** So the evaluator implements every constraint keyword the
schemas use, and its correctness is verified empirically — the whole official OpenAPI example corpus
(22 JSON+YAML documents across 3.0/3.1/3.2) produces **zero** schema violations. Unresolvable external
references evaluate as satisfied, so the stage never manufactures a violation from an unresolved `$ref`.

## AOT posture

Model traversal plus `System.Text.Json` document reading for the schema stage — no reflection-based
serialization, no dynamic code. Trimming- and NativeAOT-safe.

## Non-goals

- A general-purpose JSON Schema library. The evaluator targets the OAS meta-schemas only; it is not a
  standalone 2020-12 implementation (no `$vocabulary`, no remote `$ref`, no format assertion).
- Cross-document `$ref` resolution and bundling. Internal `#/components/...` references are resolved
  where a rule needs them (path parameters); full external resolution is a later concern.
- Deep version-placement traversal of every nested inline schema. Document-, operation-, component-,
  tag-, and security-scheme placements are validated; recursive descent into nested schema graphs is a
  follow-up (the schema-conformance stage covers deep structure for callers who opt in).
