# Assimalign.Cohesion.OpenApi.Integration — Design

## Design intent

Let the Web layer and ApiManager feed and consume OpenApi descriptions **through contracts**, so the
integration lives at the correct boundary and no service-runtime concern leaks into the model. This
project owns the seam; the service layers implement or call it. The OpenApi model, serialization,
validation, generation, and versioning libraries never learn that Web or ApiManager exist.

## The boundary, and which side depends on which

```
        Web layer  ── implements ──▶  IOpenApiEndpointSource ──┐
                                                               ├──▶ IOpenApiDescriptionProvider ──▶ OpenApiDocument
     (source-generated metadata,          Integration project ─┘        (composes + generates)
      no reflection)

     ApiManager  ── calls ──▶  IOpenApiDocumentImporter / IOpenApiDocumentExporter
```

- **Web → OpenApi** is *inversion of dependency*: Web depends on this project and implements
  `IOpenApiEndpointSource`, exposing its routes as the transport-neutral intermediate metadata (the same
  `OpenApi*Metadata` records the attribute model and source generator already produce). This project — not
  Web — knows how to turn that metadata into a document. So the OpenApi family has **no** dependency on
  Web, and Web contributes only plain data.
- **ApiManager → OpenApi** is *consumption*: ApiManager calls `IOpenApiDocumentImporter` /
  `IOpenApiDocumentExporter`, thin seams over the serialization readers/writers and the transform
  pipeline. ApiManager never touches those internals directly.

## Why reuse the intermediate metadata as the Web contract

`IOpenApiEndpointSource` yields `OpenApiOperationMetadata`, `OpenApiSchemaMetadata`, … rather than a new
descriptor type. This is deliberate: a real Web layer already gets that metadata for free from the
source generator (feature .06), so the endpoint source is a trivial adapter over the generated
`OpenApiMetadataRegistry` — the integration test's `GeneratedEndpointSource` is exactly that shape. The
whole Web path (annotated endpoints → generated registry → endpoint source → provider → document) is
therefore reflection-free and NativeAOT-safe end to end, which the tests prove by driving it through
validation and serialization.

## Version targeting and lossy export

`IOpenApiDescriptionProvider.GetDocument(version)` targets a line, so a Web layer can serve 3.0, 3.1, or
3.2 from one annotated codebase. `IOpenApiDocumentExporter.Export(document, format, version)` retargets
through the transform pipeline and returns the diagnostics, so an ApiManager retarget that drops a
construct is reported rather than silent — the export result carries the same `OpenApiTransformDiagnostic`
values the versioning library produces.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Composition is plain data assembly and delegation;
no reflection, no dynamic code. The contracts are designed so implementations (a Web endpoint source)
supply metadata produced without runtime reflection.

## Non-goals

- Hosting, routing, DI wiring, or the `/openapi.json` endpoint itself — those belong to the Web layer
  that implements the contract. This project stops at producing the document.
- ApiManager policy semantics — the import/export/transform seams are provided; policy-aware processing
  is ApiManager's concern, built on these seams.
- Runtime reflection-based endpoint discovery — the contract is satisfied by source-generated metadata,
  keeping the path AOT-safe.
