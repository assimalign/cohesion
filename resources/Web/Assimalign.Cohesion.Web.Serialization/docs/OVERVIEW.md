# Assimalign.Cohesion.Web.Serialization — Overview

The content-serialization registry for the Cohesion Web pipeline: how request and response
bodies are formatted. Applications register serialization once at builder time, keyed by media
type, and middleware/handlers read and write typed bodies with no per-call-site serializer
ceremony — reflection-free under NativeAOT.

## What it provides

- **The registry** — `IHttpContentSerializationFeature`, a typed feature on every exchange with
  distinct request-deserialization (`IHttpContentReader`) and response-serialization
  (`IHttpContentWriter`) halves, keyed by `HttpMediaType`.
- **Builder-time registration** — `AddJsonSerialization(AppJsonContext.Default)` registers the
  built-in JSON pair over a source-generated `IJsonTypeInfoResolver`;
  `AddContentSerialization()` + `ContentSerializationBuilder` register custom formats.
- **Typed call sites** — `request.ReadContentAsync<T>()` and
  `response.WriteContentAsync(value)` extensions that dispatch through the registry.

## Usage

```csharp
// Composition (builder time) — the resolver is the application's source-generated context.
builder.AddJsonSerialization(AppJsonContext.Default);

// A handler (request time) — no JsonTypeInfo ceremony at the call site.
application.Use(async (context, next) =>
{
    Order? order = await context.Request.ReadContentAsync<Order>(context.RequestCancelled);

    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.WriteContentAsync(Receipt.For(order!), context.RequestCancelled);
});
```

## Scope boundaries

- **Content negotiation** (selecting the response format from `Accept`) composes *over* this
  registry — it is #149's scope and plugs into the same `HttpMediaType` seam.
- **Source-generated binding and validation** (#796) consume the registry for body IO;
  validation sits between deserialization and the handler, outside this package.
- **Error surfacing** is the `Web.ErrorHandling` `OnError` hook's scope; this package only
  *throws* well-defined faults (`HttpContentSerializationException`) for it to handle.

Design rationale lives in [DESIGN.md](DESIGN.md).
