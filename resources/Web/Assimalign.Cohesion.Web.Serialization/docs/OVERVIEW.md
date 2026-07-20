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
- **Content negotiation** — `context.WriteNegotiatedContentAsync(value)` selects the response
  format from the request's `Accept` header (over the same registry, reusing the #771 negotiation
  primitive), stamps `Vary: Accept`, and composes a bodyless `406` when nothing is acceptable;
  `feature.TryNegotiate(acceptHeader, out mediaType)` is the underlying non-throwing seam.

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

// Content negotiation — let the request's Accept header pick the response format.
application.Use(async (context, next) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;

    // Negotiates over the registered writers, stamps Vary: Accept, and returns false after
    // setting a bodyless 406 when the client accepts nothing the registry can produce.
    await context.WriteNegotiatedContentAsync(Receipt.Latest, context.RequestCancelled);
});
```

## Scope boundaries

- **Content negotiation** (selecting the response format from `Accept`) is delivered here as a
  thin layer *over* the registry (#149), reusing the shared `HttpContentNegotiation` primitive; it
  adds no matching rules of its own beyond a narrow structured-suffix fallback. `Accept-Charset` /
  `Accept-Language` and the client-side half stay out of scope (see DESIGN non-goals).
- **Source-generated binding and validation** (#796) consume the registry for body IO;
  validation sits between deserialization and the handler, outside this package.
- **Error surfacing** is the `Web.ErrorHandling` `OnError` hook's scope; this package only
  *throws* well-defined faults (`HttpContentSerializationException`) for it to handle.

Design rationale lives in [DESIGN.md](DESIGN.md).
