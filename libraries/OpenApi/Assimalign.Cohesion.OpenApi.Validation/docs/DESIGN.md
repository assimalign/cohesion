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
| `StructuralValidationRule` | Required fields, path-template shape, mutually exclusive fields | `OPENAPI1001`–`OPENAPI1003` |
| `VersionPlacementRule` | Fields/features used outside the versions that support them | `OPENAPI2001` |
| `SemanticValidationRule` | operationId uniqueness, path-parameter consistency, parameter shape, response keys, security-scheme references | `OPENAPI3001`–`OPENAPI3007` |

Codes are stable and published in `OpenApiValidationRuleCodes` (`1xxx` structural, `2xxx` version,
`3xxx` semantic) so tests and consumers can assert on them. Diagnostic locations are JSON Pointers
(RFC 6901), e.g. `#/paths/~1pets~1{id}/get/operationId`.

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

## Official-schema stage as an extension point

The acceptance criteria call for an official-schema validation stage. A full JSON Schema 2020-12
conformance engine is itself a large effort, so Wave 1 ships the **seam** rather than the engine: a
schema rule is just another `IOpenApiValidationRule`, added through
`OpenApiValidation.Create([.. OpenApiValidation.DefaultRules(), schemaRule])`. The built-in pipeline
covers structural + semantic + version placement; the official-schema rule (issue #536) and the
official example/upgrade compliance corpus are the documented follow-ups.

## AOT posture

Pure model traversal — no reflection, no dynamic code. Trimming- and NativeAOT-safe.

## Non-goals (Wave 1)

- A JSON Schema conformance engine (extension point only).
- Cross-document `$ref` resolution. Reference *shape* and internal security-scheme references are
  checked; full external reference resolution is a later concern.
- Deep version-placement traversal of every nested inline schema. Document-level, operation-level,
  component-level, tag, and security-scheme placements are validated; recursive descent into nested
  schema graphs (e.g. a gated keyword three properties deep) is a follow-up.
