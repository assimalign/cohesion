# Assimalign.Cohesion.Http.Forms

HTTP form-body model for the Cohesion HTTP family.

## Why a separate package

The Cohesion HTTP protocol core (`Assimalign.Cohesion.Http`) defines
the wire model &mdash; status, headers, methods, target, raw body
stream. Form-body parsing interprets a request body under one of two
content types (`application/x-www-form-urlencoded`,
`multipart/form-data`) and produces a typed collection of key/value
pairs plus uploaded files.

That parsing is **application code**, not protocol mechanics. A
protocol-only consumer (an HTTP client, a reverse proxy, an edge
cache, the DNS-over-HTTPS transport) has no use for it. Keeping the
form model out of the core lets the core stay small and AOT-friendly
while making form support an explicit opt-in.

The familiar property-style `request.Form` access (on `IHttpRequest`)
and the lazy `context.ReadFormAsync()` parse entry point (on `IHttpContext`)
are restored here through a .NET 10 extension property + extension method,
both backed by an `IHttpFormFeature` stored in `IHttpContext.Features`.

## Surface

| Type | Role |
|------|------|
| `IHttpFormCollection` | Key &rarr; value bag plus uploaded files |
| `IHttpFormFile` | Single uploaded file |
| `IHttpFormFileCollection` | Collection of uploaded files |
| `HttpFormCollection` | Default in-memory implementation |
| `HttpFormFile` | Default in-memory file implementation |
| `HttpFormFileCollection` | Default in-memory file collection |
| `IHttpFormFeature` | Per-exchange parsed-form state stored in `IHttpContext.Features` |
| `HttpFormFeature` | Default in-memory feature implementation (internal; constructed via `Form` setter / `ReadFormAsync`) |
| `HttpContextFormExtensions` | `request.Form` extension property on `IHttpRequest` + `context.ReadFormAsync(...)` extension method on `IHttpContext` |

## Usage

```csharp
using Assimalign.Cohesion.Http;

// Once form parsing is wired up, callers ask the context for the parsed
// form directly:
IHttpFormCollection form = await context.ReadFormAsync(cancellationToken);

string? user = form["user"];
foreach (IHttpFormFile file in form.Files)
{
    using Stream stream = file.OpenReadStream();
    // ...
}

// Pre-attach a parsed collection (e.g. from a test fixture or a custom
// middleware that does its own body buffering) directly on the request:
HttpFormCollection prebuilt = new();
prebuilt.Add("name", "cohesion");
context.Request.Form = prebuilt;

// Subsequent reads see the same instance:
IHttpFormCollection same = await context.ReadFormAsync();    // returns prebuilt
IHttpFormCollection alsoSame = context.Request.Form!;        // returns prebuilt
```

## Why `request.Form` reaches through to the context

The `Form` extension property hangs off `IHttpRequest` so call sites read
naturally (`context.Request.Form` mirrors the wire-level intuition that a
form is request-side state). Storage, however, is the strongly-typed
`IHttpContext.Features` collection &mdash; the same place sessions and
authentication features live &mdash; reached through the
`IHttpRequest.HttpContext` back-reference. That keeps a single
type-keyed extensibility seam for all higher-layer features without
duplicating it onto every wire-level type.

## Implementing a custom feature

`HttpFormFeature` is internal. Middleware that needs richer form-handling
behaviour (streaming uploads, alternate buffering strategies, in-process
caching) should implement `IHttpFormFeature` directly and attach it via
`context.Features.Set<IHttpFormFeature>(...)`. The `context.Form` getter
and `context.ReadFormAsync(...)` extension method consult the feature
collection for any implementation, not just the package's default.

## Status

**Type contracts and an in-memory model ship today.** The actual
multipart / urlencoded parser is staged for a follow-up PR &mdash;
the prior implementation lived inside `Assimalign.Cohesion.Http.Connections`'s
per-version message readers and was removed together with the dead
`IHttpRequest.Form` property when the protocol/web boundary moved.
Until the parser lands, `ReadFormAsync` returns an empty collection
when nothing has been pre-attached; consumers that already have a
parsed collection can install it via `context.Form = ...`.
