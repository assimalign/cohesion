# Assimalign.Cohesion.OpenApi.Fluent — Overview

A fluent authoring layer for the OpenApi model. `OpenApiDocumentBuilder.Create(version, title, apiVersion)`
returns an `OpenApiDocumentBuilder` (a concrete class — see the DESIGN note on why this library is not
interface-first); chain members and nested `Action<T>` callbacks to author a document, then call
`Build()` to get an `OpenApiDocument`.

## Scope

- Version-targeted builders for documents, info, servers, paths, operations, parameters, request
  bodies, responses, media types, components, schemas, security schemes, examples, tags, and the
  advanced surfaces (callbacks, links, webhooks, externalDocs).
- Version-gated members throw `OpenApiException` at authoring time when the target line does not
  support them.

## Dependencies

- `Assimalign.Cohesion.OpenApi` (the model). No serialization or validation dependency.

## Usage

```csharp
using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Fluent;

var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Petstore", "1.0.0")
    .Info(i => i.Description("A sample API").License("MIT"))
    .Server("https://api.example.com")
    .Path("/pets", path => path
        .Operation(OperationType.Get, op => op
            .OperationId("listPets")
            .Response("200", r => r.Description("A list of pets"))))
    .Build();
```

See [DESIGN.md](./DESIGN.md) for the builder shape and version-awareness model.
