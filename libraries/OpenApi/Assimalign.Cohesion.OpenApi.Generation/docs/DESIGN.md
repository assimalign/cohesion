# Assimalign.Cohesion.OpenApi.Generation — Design

## Design intent

The emission half of the attribute path: turn the flat intermediate metadata (from the source
generator or the runtime attribute mapper) into a canonical, version-targeted `OpenApiDocument`. The
output is an ordinary model graph, so a generated document composes with fluent- or hand-built content
and serializes and validates like any other.

## Position in the family

```
[attributes] → source generator → OpenApiMetadataRegistry → OpenApiGenerationInput
                                                                     │
                                                    OpenApiDocumentGenerator.Generate
                                                                     │
                                                             OpenApiDocument → JSON/YAML
```

`OpenApiGenerationInput` is the seam: the source generator emits a registry of the metadata records
(feature .06's generator), and this pipeline consumes them. Because the input is plain data, the same
pipeline is exercised in tests from hand-built metadata and from the runtime attribute mapper's output —
the generator's compile-time path and the runtime path converge on one emitter.

## Version-targeted, version-clean output

`OpenApiGenerationOptions.Version` sets the target line, and the generator populates version-gated fields
**only when the target supports them**, consulting the same `OpenApiVersionCapabilities` matrix the
serializer and validator use:

- Tag `summary`/`parent`/`kind` are set only for 3.2 targets.
- A `query` operation is dropped for targets below 3.2 rather than emitted into a document that could
  not represent it.

The result is that a generated document is *version-clean*: it passes `document.Validate()` for its
declared line without the serializer having to silently drop fields. This is deliberate — the generator
owns version targeting for the metadata it emits; broader cross-version transforms are feature .07.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). The pipeline is plain object construction over
the metadata records — no reflection, no dynamic code. Combined with the source generator (which emits
the metadata as static data, also reflection-free), the whole attribute → document path is
NativeAOT- and trimming-safe end to end, which the generator test project proves by driving the
generated registry through the pipeline.

## Non-goals

- Cross-version transforms (3.0↔3.1↔3.2) — feature .07. This pipeline targets a single line per call.
- Structural schema inference from CLR types — a body schema is referenced by component name; the
  matching `[OpenApiSchema]` model produces the component. The pipeline does not reflect over types.
- A registration/hosting API — how a service invokes generation (an endpoint, a build step) is a
  Web/ApiManager integration concern (feature .10).
