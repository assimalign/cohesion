# Assimalign.Cohesion.OpenApi.Integration — Overview

Integration contracts that let the Web layer and ApiManager feed and consume OpenApi descriptions
without leaking service-runtime concerns into the model.

## Scope

- `IOpenApiEndpointSource` — the contract a Web layer implements to expose its routes as intermediate
  metadata.
- `IOpenApiDescriptionProvider` — composes endpoint sources into a version-targeted document.
- `IOpenApiDocumentImporter` / `IOpenApiDocumentExporter` — the ApiManager consume/produce seams, with a
  version-retargeting export that reports lossy diagnostics.
- `OpenApiIntegration` — the static factory for the above.

## Dependencies

- The OpenApi family: model, attributes (metadata), generation, serialization, versioning. No Web or
  ApiManager dependency — those implement/consume these contracts.

## Usage

```csharp
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Integration;

// Web: expose the source-generated metadata and serve a version-targeted document.
IOpenApiDescriptionProvider provider = OpenApiIntegration.CreateProvider(
    new OpenApiDescriptionInfo { Title = "Petstore", ApiVersion = "1.0.0" },
    new MyGeneratedEndpointSource());
OpenApiDocument document = provider.GetDocument(OpenApiSpecVersion.V3_1);

// ApiManager: import, retarget, export.
IOpenApiDocumentImporter importer = OpenApiIntegration.CreateImporter();
IOpenApiDocumentExporter exporter = OpenApiIntegration.CreateExporter();
var imported = importer.Import(content, OpenApiFormat.Json);
OpenApiExportResult exported = exporter.Export(imported, OpenApiFormat.Yaml, OpenApiSpecVersion.V3_0);
```

See [DESIGN.md](./DESIGN.md) for the boundary and dependency-direction rationale.
