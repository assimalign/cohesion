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

## Surface

| Type | Role |
|------|------|
| `IHttpFormCollection` | Key &rarr; value bag plus uploaded files |
| `IHttpFormFile` | Single uploaded file |
| `IHttpFormFileCollection` | Collection of uploaded files |
| `HttpFormCollection` | Default in-memory implementation |
| `HttpFormFile` | Default in-memory file implementation |
| `HttpFormFileCollection` | Default in-memory file collection |
| `HttpRequestFormExtensions` | `GetForm()` / `SetForm()` / `ReadFormAsync()` extensions on `IHttpRequest` |

## Usage

```csharp
using Assimalign.Cohesion.Http;

// Once form parsing is wired up, callers ask the request for the
// parsed form directly:
IHttpFormCollection form = await request.ReadFormAsync(cancellationToken);

string? user = form["user"];
foreach (IHttpFormFile file in form.Files)
{
    using Stream stream = file.OpenReadStream();
    // ...
}
```

## Status

**Type contracts and an in-memory model ship today.** The actual
multipart / urlencoded parser is staged for a follow-up PR &mdash;
the prior implementation lived inside `Assimalign.Cohesion.Http.Transports`'s
per-version message readers and was removed together with the dead
`IHttpRequest.Form` property when the protocol/web boundary moved.
Until the parser lands, `ReadFormAsync` returns an empty collection
when nothing has been pre-attached; consumers that already have a
parsed collection can install it via `SetForm`.
