# Assimalign.Cohesion.OpenApi.Fluent — Design

## Design intent

An ergonomic, version-aware layer for authoring `OpenApiDocument` graphs in code. It sits on top of the
canonical model and produces the same model types — it is a construction convenience, not a parallel
representation. A developer authors a document by chaining builder members and nested `Action<T>`
callbacks; the result is an ordinary `OpenApiDocument` that serializes and validates like any other.

## Shape: concrete builders, nested callbacks

> **Deviation from AGENTS.md interface-first (deliberate, scoped to this library).** The builders are
> `public sealed class`es, not interfaces. OpenAPI is a published standard, so consumers routinely bring
> their own authoring abstractions; imposing an `IOpenApi*Builder` contract here would be over-abstraction
> that competes with theirs. These concrete builders are a convenience *over the public model* — a caller
> is free to ignore them and construct `OpenApiDocument` directly, or write their own builder against the
> same model. The entry point carries a `// Deviates from AGENTS.md …` marker so the choice isn't
> "corrected" back. This deviation is scoped to the Fluent library only; the rest of the OpenApi family
> stays interface-first for its behavioral seams (`IOpenApiReader`, `IOpenApiValidator`, …).

Every builder is a `public sealed class` (`OpenApiSchemaBuilder`, `OpenApiOperationBuilder`, …) with a
public constructor and a public `Build()`. The single entry is
`OpenApiDocumentBuilder.Create(version, title, apiVersion)`, which returns `OpenApiDocumentBuilder`. Child
structures are configured through `Action<ChildBuilder>` callbacks, so the whole document reads as one
nested expression and no raw model type is touched by the caller:

```csharp
var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Petstore", "1.0.0")
    .Path("/pets/{id}", path => path
        .Operation(OperationType.Get, op => op
            .OperationId("getPet")
            .Parameter("id", ParameterLocation.Path, p => p.Schema(s => s.Type(SchemaType.Integer)))
            .Response("200", r => r.Description("A pet")
                .Content("application/json", m => m.SchemaReference("#/components/schemas/Pet")))))
    .Components(c => c.Schema("Pet", s => s.Type(SchemaType.Object).Property("name", p => p.Type(SchemaType.String))))
    .Build();
```

Method naming follows the model: bare noun-phrase members (`Type`, `Description`, `OperationId`) set a
field; `Parameter`/`Response`/`Schema`/`Property` add or nest a child. Every member returns the builder
for chaining; the terminal `Build()` returns the immutable model.

## Version awareness: fail fast at authoring time

The target OpenAPI line is fixed at `Create` and threaded into every child builder. A version-gated
member (`Self` on the document, `Summary` on a tag, `Const` on a schema, the `Query` operation, the
`querystring` location, …) checks `OpenApiVersionCapabilities.Supports` and throws `OpenApiException`
*immediately* when the target line does not support it. This is the "diagnose unsupported combinations
early" the feature asks for: the mismatch surfaces at the exact call site that authored it, rather than
as a validation diagnostic on the finished document. The guard consults the same capability matrix the
serializer and validator use, so the three never disagree about what a line allows.

Path parameters default `required` to `true` when their location is `Path`, removing the single most
common authoring papercut.

## Boundaries

- The builder does **not** re-validate the finished document — that is the validation library's job.
  It only enforces the version constraints it can check locally at authoring time. A fluently built
  document is still expected to pass `document.Validate()`, which the tests assert.
- The builder covers the authoring surface end to end: documents, paths, operations, parameters, request
  bodies, responses, media types, components, schemas, security schemes, examples, tags, info, and the
  advanced description surfaces — callbacks (`operation.Callback` over runtime-expression path items),
  links (`response.Link`/`LinkReference`, `components.Link`), webhooks (`document.Webhook`), and
  `externalDocs` on the document, operations, tags, and schemas. The remaining low-level corners
  (encoding, discriminator, XML node types) are reached by dropping to the model on the built document or
  via the `Extension` escape hatch — the fluent layer never blocks the model.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Plain object construction and delegates — no
reflection, no dynamic code.

## Non-goals

- Round-tripping *from* a model back into builder calls (the builder is author-only).
- A separate immutable builder result type — `Build()` returns the canonical `OpenApiDocument`.
- Exhaustive coverage of every model field; the escape hatches above cover the long tail.
