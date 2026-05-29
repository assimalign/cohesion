# Assimalign.Cohesion.OpenApi — Design

## Design intent

A single, version-aware object model that can represent the union of the officially published
OpenAPI 3.0.x, 3.1.x, and 3.2.x description surfaces. Callers author one graph; the line a document
targets is recorded on `OpenApiDocument.SpecVersion`, and downstream packages (serialization,
validation) adapt behavior per line by consulting one shared **capability matrix** rather than
re-deriving version differences from scattered conditionals.

This package is the foundation of the OpenApi family. It carries **no serialization-format and no
service-runtime concerns** — those live in sibling packages — so the model stays reusable by fluent
authoring, attribute/source-generation, and Web/ApiManager integration without dragging in JSON, YAML,
or hosting dependencies.

## Why a concrete data model (not interface-first)

The repo's interface-first rule (`AGENTS.md`) targets *behaviors and services* — things with swappable
implementations. An OpenAPI document is **data**, not behavior: there is exactly one shape for an `Info`
Object or a `Schema` Object. Modeling each element as a public concrete class mirrors the repo's
existing wire/data models (`DnsMessage`, `DnsRecord`, `HttpCookie`) and keeps authoring ergonomic
(object initializers, collection initializers).

The *behaviors* in the family — reading, writing, validating — **are** interface-first
(`IOpenApiReader`, `IOpenApiWriter`, `IOpenApiValidator`, `IOpenApiValidationRule`) with `internal`
implementations, exactly as the rule intends.

> A future session should not "correct" the element types into interfaces. The concreteness is
> deliberate and matches the data-model precedent in this repo.

## Unified superset model + capability matrix

One set of element types carries every field from all three lines. Version-specific behavior is made
explicit through two cooperating pieces:

- `OpenApiSpecVersion` — the line a document targets (`V3_0`, `V3_1`, `V3_2`).
- `OpenApiVersionCapabilities` — the **single source of truth** mapping each `OpenApiFeature` to the
  versions that support it, plus the canonical version string for each line and a parser from the raw
  `openapi` field. Serialization gates field *emission* on it; validation gates field *placement* on
  it. Keeping both consumers on one matrix is what prevents the writer and the validator from drifting.

### Version capability matrix (Story L01.01.15.01.03)

| Feature (`OpenApiFeature`) | 3.0.4 | 3.1.2 | 3.2.0 | Notes |
|---|:---:|:---:|:---:|---|
| `InfoSummary` | – | ✅ | ✅ | `info.summary` |
| `LicenseIdentifier` | – | ✅ | ✅ | SPDX `license.identifier` (excl. with `url`) |
| `Webhooks` | – | ✅ | ✅ | top-level `webhooks` |
| `JsonSchemaDialect` | – | ✅ | ✅ | top-level `jsonSchemaDialect` |
| `ComponentsPathItems` | – | ✅ | ✅ | `components.pathItems` |
| `ReferenceSummaryAndDescription` | – | ✅ | ✅ | `summary`/`description` beside `$ref` |
| `MutualTlsSecurityScheme` | – | ✅ | ✅ | `type: mutualTLS` |
| `SchemaNullableKeyword` | ✅ | – | – | 3.0 `nullable`; 3.1+ uses a `"null"` type entry |
| `SchemaTypeArray` | – | ✅ | ✅ | `type: [..., "null"]` |
| `SchemaNumericExclusiveBounds` | – | ✅ | ✅ | numeric `exclusiveMinimum/Maximum` (3.0 = boolean) |
| `SchemaConst` | – | ✅ | ✅ | JSON Schema `const` |
| `SchemaExamples` | – | ✅ | ✅ | schema-level `examples` array |
| `PathItemAdditionalOperations` | – | – | ✅ | `additionalOperations` |
| `DocumentSelf` | – | – | ✅ | top-level `$self` |
| `TagExtendedMetadata` | – | – | ✅ | tag `summary`/`parent`/`kind` |
| `OAuthDeviceAuthorizationFlow` | – | – | ✅ | OAuth `deviceAuthorization` flow |

### Two normalized version differences

Rather than expose every wire form, the model normalizes the two cases that would otherwise force
callers to think per-version. The serializer adapts them on the way out:

- **Nullability** — author `Type` + `Nullable`. 3.0 emits the `nullable` keyword; 3.1+ emits a type
  array (`["string","null"]`).
- **Exclusive bounds** — `ExclusiveMinimum`/`ExclusiveMaximum` are numeric. 3.0 emits the paired
  `minimum`/`maximum` value plus a boolean flag; 3.1+ emits the numeric keyword directly.

> Non-goal for Wave 1: arbitrary multi-type unions beyond the nullable idiom (e.g.
> `type: ["string","integer"]`). The `Type` + `Nullable` shape covers the dominant case; broader unions
> are a documented follow-up.

## The neutral node tree (`OpenApiNode`)

`example`, `default`, `enum`, `const`, and specification-extension (`x-`) values are arbitrary data.
They are represented by the format-agnostic `OpenApiNode` tree (`OpenApiObjectNode`, `OpenApiArrayNode`,
`OpenApiValueNode`) that lives here in the model. This is a *data* concern, not a *format* concern, so
it belongs to the model.

Critically, the serialization package reuses this same tree as its intermediate representation for the
**entire** document: model → node tree (all version logic) → text. JSON and YAML differ only in the
final node-tree-to-text renderer. That is the seam that makes a YAML reader/writer a drop-in addition
(tracked as the required fast-follow, issue #532) without touching the model or the version logic.

## Error model

Recoverable problems (missing required fields, version mismatches, unresolved references) are **not**
thrown — they are reported as `OpenApiDiagnostic` values by the validation package. `OpenApiException`
(area-scoped root, inherits `Exception` per the AGENTS area-root rule) is reserved for hard programming
errors such as a structurally impossible node.

## Suggested project family and ownership boundaries (Story L01.01.15.01.04)

Advisory architecture for the full OpenApi family. Dependency direction is strictly one-way — every
package depends on the root model; no package depends on a sibling except through the model.

| Package | Responsibility | Depends on | Status |
|---|---|---|---|
| `Assimalign.Cohesion.OpenApi` | Canonical model, version capability matrix, node tree | — | **this wave** |
| `…OpenApi.Serialization` | Model ↔ node-tree mapping; JSON I/O (YAML fast-follow) | model | **this wave** |
| `…OpenApi.Validation` | Diagnostics + structural/semantic/version rules | model | **this wave** |
| `…OpenApi.Fluent` | Fluent authoring builders | model | Wave 2 |
| `…OpenApi.Attributes` | Attribute metadata model | model | Wave 2 |
| `…OpenApi.SourceGeneration` | AOT-safe attribute discovery → descriptors | attributes, model | Wave 2 |
| `…OpenApi.Generation` | Generation orchestration | model, serialization, attributes | Wave 2 |

Boundaries that exist to preserve specific properties: the model stays free of serialization so YAML
and future formats are additive; attribute discovery is isolated so it can be source-generated (no
runtime reflection, NativeAOT-safe); validation is separable so the official-schema conformance stage
can plug in without bloating the model; and the model carries no hosting concern so Web/ApiManager
integration is built through adapters, not by leaking request-pipeline types inward.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). The model is plain data with no reflection, no
dynamic code, and no runtime serialization. Serialization uses `System.Text.Json`'s `Utf8JsonReader`/
`Utf8JsonWriter` and `JsonDocument` only — no reflection-based (de)serialization, so the whole family
is trimming- and NativeAOT-safe.

## Non-goals (Wave 1)

- Fluent authoring, attribute model, source generation, version *transforms*, advanced authoring
  surfaces, official-example compliance corpus, and Web/ApiManager integration (later waves).
- YAML I/O — architected for but deferred to the immediate fast-follow (#532).
- A full JSON Schema 2020-12 conformance engine — the official-schema validation stage is an exposed
  extension point, not yet implemented.
