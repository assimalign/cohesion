# Assimalign.Cohesion.OpenApi.Serialization

JSON serialization for the Cohesion OpenApi document model. Maps the canonical `OpenApiDocument` graph to
and from a format-agnostic `OpenApiNode` tree (version-aware, gated by the model's capability matrix) and
renders that tree to and from JSON using `System.Text.Json` with no reflection.

```csharp
string json = document.ToJson(OpenApiSpecVersion.V3_1);
OpenApiDocument parsed = OpenApiJson.Parse(json);
```

The neutral node-tree seam is deliberately format-independent so a YAML reader/writer can be added beside
the JSON one without changing the model mapping (the required fast-follow). NativeAOT- and trimming-safe.

See `docs/DESIGN.md` for the serialization pipeline and the YAML seam.
