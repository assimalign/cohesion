# Assimalign.Cohesion.OpenApi.Versioning — Design

## Design intent

Convert a document between the supported OpenAPI lines and tell the caller exactly what the conversion
costs. An upgrade (3.0 → 3.1 → 3.2) is mostly non-lossy; a downgrade drops constructs the older line
cannot represent. Every conversion and every dropped construct is reported as an
`OpenApiTransformDiagnostic`, so a migration is auditable rather than silent.

## Why most of 3.0↔3.1 needs no transform

The model already normalizes the two differences that dominate a 3.0↔3.1 conversion:

- **Nullability** is stored as `Type` + `Nullable`; the serializer emits `nullable: true` for 3.0 and a
  `["…","null"]` type array for 3.1+.
- **Exclusive bounds** are stored numerically; the serializer emits the 3.0 boolean-flag form or the
  3.1 numeric form.

So a document whose only version-specific content is nullability or exclusive bounds transforms by
*re-stamping the version* — the serializer does the rest. The transformer proves this with tests rather
than duplicating the logic.

## What the transformer actually does

1. **Deep copy** the input via a serialization round-trip at the **superset line** (`ToJson(V3_2)` →
   `Parse`), so the input is never mutated and no construct is gated away by the writer. Copying at the
   source line would be lossy on an upgrade — a 3.1-only construct carried on a 3.0-declared document
   (a list-valued `examples`, a `const`, `$defs`, …) would be dropped before the transform ran. The
   model normalizes the only two version-specific wire forms (nullability, exclusive bounds), so a 3.2
   round-trip preserves every model field.
2. **Walk every schema** (`OpenApiSchemaWalker`) and apply the construct changes the version delta
   requires. The walker visits every schema-bearing location — component maps (schemas, parameters,
   headers, request bodies, responses, media types, callbacks, path items), path and webhook operations
   (including path-item-level and `content`-map parameters and additional operations), response and
   encoding headers, streaming `itemSchema`s, and callbacks — then recurses through composition,
   properties, items, and the 2020-12 subschema keywords, so a conversion applies wherever a schema
   lives, not only under `components/schemas`. The changes:
   - *3.0 → 3.1*: `format: byte`/`binary` → `contentEncoding`/`contentMediaType`; singular schema
     `example` → the `examples` array.
   - *3.1 → 3.2*: deprecated XML `attribute`/`wrapped` flags → `nodeType`.
   - *downgrade to 3.0*: `const` → a single-value `enum`; the `examples` array → a singular `example`;
     a multi-type schema → its first type (a warning — this one is lossy).
   - *downgrade from 3.2*: `nodeType` → the deprecated `attribute`/`wrapped` flags.
3. **Analyze version fit** by running the validator on the re-stamped copy and lifting every
   `UnsupportedInVersion` finding into a transform warning. This reuses the *same capability matrix* the
   serializer and validator share, so the transformer never maintains its own list of what each line
   supports — the constructs a downgrade drops (webhooks, `$self`, `additionalOperations`, mutualTLS, …)
   are reported automatically, located by JSON Pointer.

## The capability matrix is the single version contract

Feature .07's "explicit version targets and capability gates" is not a new mechanism — it is
`OpenApiVersionCapabilities`, already the one source of truth consumed by serialization (emission),
validation (placement), fluent authoring (early diagnosis), and generation (version-clean output). The
transformer is the fifth consumer: it targets a version and reports the fit through that same matrix.
Keeping one contract is what guarantees an upgraded document validates and serializes cleanly for its
new line.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Plain model traversal plus a serialization
round-trip; no reflection, no dynamic code.

## Non-goals

- Rewriting specification extensions (e.g. `x-displayName` → tag `summary`, `x-tagGroups` → tag
  `parent`). The official 3.1 → 3.2 guidance suggests these, but they live in the untyped extension bag;
  translating them is a documented follow-up, not silent behavior.
- Resolving or bundling external `$ref`s before transforming — the transform is per-document.
- Guaranteeing a byte-identical document to another tool's upgrade output; the guarantee is a
  semantically-equivalent, version-clean document plus a full diagnostic trail.
- Individually reporting *non-convertible* schema keywords dropped from a **nested** schema on a
  downgrade (e.g. a `$defs` block three properties deep, dropped for 3.0). Convertible constructs
  (`const`, multi-type, `example`/`examples`, binary formats, XML node types) are rewritten wherever
  they occur; but the "unsupported and dropped" analysis leans on the validator's version-placement
  rule, which — by its own documented non-goal — checks component schemas and document/operation/security
  levels, not the interior of nested schema graphs. Component- and top-level drops are reported; a
  keyword buried inside an inline schema is silently gated out on serialization. Deep schema-placement
  analysis is the same follow-up the validation library tracks.
