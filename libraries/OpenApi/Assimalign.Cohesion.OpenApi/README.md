# Assimalign.Cohesion.OpenApi

The canonical, version-aware OpenAPI description object model for the Cohesion OpenApi family. Represents
the union of the officially published OpenAPI **3.0.4**, **3.1.2**, and **3.2.0** surfaces as a single
object graph, with an explicit version capability matrix (`OpenApiVersionCapabilities`) that gates fields
and semantics per line.

This package carries no serialization-format or service-runtime concerns; it is the foundation that the
serialization, validation, and (later) fluent, attribute, source-generation, and generation packages
build on.

```csharp
var document = new OpenApiDocument
{
    SpecVersion = OpenApiSpecVersion.V3_1,
    Info = new OpenApiInfo { Title = "Pets API", Version = "1.0.0" }
};
```

See `docs/DESIGN.md` for the version model and the suggested project family, and `docs/OVERVIEW.md` for
a usage summary. NativeAOT- and trimming-safe.
