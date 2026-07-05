# Assimalign.Cohesion.Http.Forms

HTTP form-body model **and streaming parser** for the Cohesion HTTP family.

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
are provided here through a .NET 10 extension property + extension method,
both backed by an `IHttpFormFeature` stored in `IHttpContext.Features`.

## Surface

| Type | Role |
|------|------|
| `IHttpFormCollection` | Key &rarr; value bag plus uploaded files |
| `IHttpFormFile` | Single uploaded file |
| `IHttpFormFileCollection` | Collection of uploaded files |
| `HttpFormCollection` | Default in-memory implementation |
| `HttpFormFile` | Default in-memory / spill-to-disk file implementation |
| `HttpFormFileCollection` | Default in-memory file collection |
| `IHttpFormFeature` | Per-exchange parsed-form state stored in `IHttpContext.Features` |
| `HttpFormFeature` | Default feature: streaming urlencoded + multipart parser (public, `sealed`) |
| `HttpFormOptions` | Per-parse limits (value/key/section sizes, spill threshold, boundary length) |
| `HttpContextFormExtensions` | `request.Form` extension property + `context.ReadFormAsync(...)` extension method |

## Usage

```csharp
using System.Threading;
using Assimalign.Cohesion.Http;

// Ask the context for the parsed form. The first call installs an
// HttpFormFeature over the request body (when none is present), parses it,
// and caches the result; later calls return the same collection.
IHttpFormCollection form = await context.ReadFormAsync(CancellationToken.None);

string user = form["user"].Value;
foreach (IHttpFormFile file in form.Files)
{
    using Stream stream = file.OpenReadStream();
    // ...
}

// Pre-attach a parsed collection (e.g. from a test fixture or a custom
// middleware that did its own body buffering) directly on the request. This
// short-circuits parsing entirely — the body stream is never touched.
HttpFormCollection prebuilt = new();
prebuilt.Add("name", "cohesion");
context.Request.Form = prebuilt;

// Subsequent reads see the same instance:
IHttpFormCollection same = await context.ReadFormAsync();    // returns prebuilt
IHttpFormCollection alsoSame = context.Request.Form;         // returns prebuilt
```

In a Web application, add the middleware from `Assimalign.Cohesion.Web.Forms`
so every request has its form pre-parsed before your handlers run:

```csharp
using Assimalign.Cohesion.Web;

builder.UseForms();   // installs HttpFormFeature + eagerly parses each request
```

## Charset handling

The urlencoded parser decodes the body using the encoding named by the
Content-Type `charset` parameter, defaulting to UTF-8 (the WHATWG URL default
for `application/x-www-form-urlencoded`). Only built-in, AOT-safe encodings are
honored &mdash; UTF-8, US-ASCII, ISO-8859-1, and UTF-16; unrecognized charsets
fall back to UTF-8 rather than throwing. `Encoding.GetEncoding` is deliberately
**not** called on arbitrary charset names because the code-pages provider it
requires is not NativeAOT-friendly. Multipart section text is always decoded as
UTF-8.

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

`HttpFormFeature` is `public sealed`, so most callers use it directly (or via
`ReadFormAsync` / the `Form` setter, which construct it for you). Middleware
that needs richer form-handling behaviour (alternate buffering strategies,
in-process caching, per-part streaming) can implement `IHttpFormFeature`
itself and attach it via `context.Features.Set<IHttpFormFeature>(...)`. The
`request.Form` getter and `context.ReadFormAsync(...)` extension consult the
feature collection for any registered implementation, not just the package's
default.

## Status

**Shipping.** Both the `application/x-www-form-urlencoded` and
`multipart/form-data` parsers are implemented as incremental, streaming readers
(`HttpFormReader`, `HttpMultipartFormReader`) with per-parse limits and
spill-to-disk buffering for large file uploads. They are wired through the
public `HttpFormFeature` and surfaced by `HttpContextFormExtensions`
(`context.ReadFormAsync`, `request.Form`) and by `UseForms()` in
`Assimalign.Cohesion.Web.Forms`. Unit tests cover both parsers, the convenience
surface, and the limits; integration tests round-trip real `HttpClient`
requests through an HTTP/1.1 listener into `HttpFormFeature`.

Known constraint: `IHttpFormCollection` stores one value per key, so a
urlencoded body that repeats a key has its values comma-joined (RFC 7230
§3.2.2). A multi-value indexer is a separate, larger API change tracked
independently. See [`docs/DESIGN.md`](docs/DESIGN.md) for the full design
rationale, error model, and non-goals.
