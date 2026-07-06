# Assimalign.Cohesion.OpenApi.Serialization — Design

## Design intent

Turn the canonical model into JSON and YAML and back without reflection. The package is organized as
three layers so that the version logic and the wire format are never entangled.

## Three layers

```
OpenApiDocument  ⇄  OpenApiNode tree  ⇄  text (JSON and YAML)
        |                   |                    |
 model↔node mapping    (the seam)         node↔text formatter
 (version-aware)                          (format-specific)
```

1. **Model ↔ node tree** (`OpenApiModelToNodeConverter`, `OpenApiNodeToModelConverter`). All version
   behavior lives here: fields unsupported by the target line are omitted on write (per
   `OpenApiVersionCapabilities`), and the two normalized differences — nullability and exclusive numeric
   bounds — are emitted in the form the target line expects. The reader detects those forms by node
   *shape* (a `"null"` type entry or a boolean `exclusiveMaximum`) so a document round-trips regardless
   of which line produced it.
2. **The node tree** (`OpenApiNode`, defined in the model package) is the format-agnostic intermediate
   representation. It is the seam: nothing above it knows about JSON or YAML.
3. **Node ↔ text** — two thin formatters. `OpenApiJsonNodeFormatter` is the only JSON-aware layer,
   built on `Utf8JsonWriter` and `JsonDocument`. `OpenApiYamlNodeFormatter` is the only YAML-aware
   layer, converting the node tree to and from the `Assimalign.Cohesion.Content.Yaml` document model
   (a Cohesion in-house YAML 1.2 engine — no third-party dependency).

## Why a node-tree intermediate (validated by the YAML formatter)

A direct model-to-`Utf8JsonWriter` writer would be marginally faster but would bake JSON assumptions
into the version logic, forcing a parallel reimplementation for YAML. Routing through a neutral node
tree meant the YAML support (issue #532) landed exactly as designed: **a second node↔text formatter
plus the `OpenApiYaml` facade, with zero changes to the model↔node mapping or any version gating.**
Scalar kinds map one-to-one (`OpenApiValueKind` ⇄ `YamlScalarKind` via the YAML core schema); strings
that would re-resolve as other kinds are pinned so kinds survive the format boundary; YAML stream
input flows through `Content.Text` Unicode encoding detection.

The cost is one extra object-tree allocation per document. For an API-description document (not a hot
path) that is a deliberate and acceptable trade for the format independence.

Parse failures surface uniformly: both readers wrap their format exception (`JsonException`,
`YamlException`) in an `OpenApiException` with the same message shape, so callers handle malformed
input consistently across formats. An OpenAPI description is one document — multi-document YAML
streams are rejected.

## Determinism and fidelity

- Object members are emitted in a stable order: model fields in a natural spec order, and map entries
  (schemas, responses, paths, …) sorted ordinally. Output is therefore deterministic across runs.
- Specification extensions (`x-`) and `$ref` references are preserved on both read and write.
- Scalar kind is preserved through the node tree. One inherent JSON ambiguity remains: a whole-number
  floating-point value (e.g. `1.0`) is written as `1` and reads back as an integer node. Typed model
  fields (e.g. `double? Maximum`) are unaffected because they read integers as numbers; only arbitrary
  `OpenApiNode` values (examples/defaults) can observe the kind change.

### Version-gated schema forms

The 3.1 full-JSON-Schema surfaces get explicit down-level treatment rather than best-effort emission:

- **Boolean schemas** (`BooleanValue`) emit the literals `true`/`false` for 3.1+; a 3.0 target gets the
  structural equivalents `{}` (true) and `{"not": {}}` (false), since 3.0 has no boolean form.
- **`$ref` siblings** are kept for 3.1+ and dropped for 3.0, where consumers would ignore them anyway;
  the validator (not the writer) is responsible for diagnosing the combination on a 3.0 document.
- **Multi-type unions** emit a type array for 3.1+; a 3.0 target gets the first type only, with the
  loss flagged by validation rather than silently accepted round-trip damage.
- **References in `content` maps** (a Media Type Object position, 3.2+) are written verbatim for every
  target — omitting the member would lose the whole media type, so a dangling-below-3.2 reference is a
  validation concern, matching how every other referenceable position behaves.

## AOT posture

`Utf8JsonReader`/`Utf8JsonWriter`/`JsonDocument` for JSON and the hand-written `Content.Yaml` engine
for YAML — no reflection anywhere, no source generator required. Fully trimming- and NativeAOT-safe.

## Non-goals

- External `$ref` resolution / document bundling — the reader preserves references verbatim; resolving
  them across documents is a later concern.
- YAML comment preservation — comments are not part of the node tree; a round-trip drops them (the
  underlying engine documents the same limit).
