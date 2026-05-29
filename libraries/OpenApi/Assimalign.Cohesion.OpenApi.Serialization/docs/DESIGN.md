# Assimalign.Cohesion.OpenApi.Serialization — Design

## Design intent

Turn the canonical model into JSON and back without reflection, while keeping the door open for YAML as
a pure drop-in. The package is organized as three layers so that the version logic and the wire format
are never entangled.

## Three layers

```
OpenApiDocument  ⇄  OpenApiNode tree  ⇄  text (JSON now, YAML next)
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
3. **Node ↔ text** (`OpenApiJsonNodeFormatter`) is the only JSON-aware layer, built on
   `Utf8JsonWriter` and `JsonDocument`.

## Why a node-tree intermediate (and the YAML fast-follow)

A direct model-to-`Utf8JsonWriter` writer would be marginally faster but would bake JSON assumptions into
the version logic, forcing a parallel reimplementation for YAML. Routing through a neutral node tree means
**YAML is purely a second node↔text formatter** (`OpenApiYamlNodeFormatter`) plus a `OpenApiYaml` facade —
zero changes to the ~600 lines of model↔node mapping or any version gating. YAML is the required
fast-follow (issue #532); this layering is the reason it is a focused, low-risk addition.

The cost is one extra object-tree allocation per document. For an API-description document (not a hot
path) that is a deliberate and acceptable trade for the format independence.

## Determinism and fidelity

- Object members are emitted in a stable order: model fields in a natural spec order, and map entries
  (schemas, responses, paths, …) sorted ordinally. Output is therefore deterministic across runs.
- Specification extensions (`x-`) and `$ref` references are preserved on both read and write.
- Scalar kind is preserved through the node tree. One inherent JSON ambiguity remains: a whole-number
  floating-point value (e.g. `1.0`) is written as `1` and reads back as an integer node. Typed model
  fields (e.g. `double? Maximum`) are unaffected because they read integers as numbers; only arbitrary
  `OpenApiNode` values (examples/defaults) can observe the kind change.

## AOT posture

`Utf8JsonReader`/`Utf8JsonWriter`/`JsonDocument` only — no `JsonSerializer` reflection, no source
generator required. Fully trimming- and NativeAOT-safe.

## Non-goals (Wave 1)

- **YAML** — architected for (the seam exists), implemented next (#532).
- External `$ref` resolution / document bundling — the reader preserves references verbatim; resolving
  them across documents is a later concern.
