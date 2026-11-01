# Assimalign.Cohesion.Web.Compression — Overview

Transparent HTTP body compression for the Cohesion Web pipeline, both directions in one lean
feature package. Cohesion serves HTTP directly in its in-process hosting story, so response
compression (bandwidth, TTFB for JSON/HTML) and safe request decompression cannot be punted to a
reverse proxy. This package fills that gap with two independent middleware, each configured where it
is registered (`builder.Services.AddResponseCompression(...)`, `builder.Services.AddRequestDecompression(...)`)
and placed by a parameterless `Use` verb, over the BCL codecs only — no external dependencies, AOT-safe.

## What it does

### Response compression — `UseResponseCompression`

- **Negotiates** gzip or Brotli from each request's `Accept-Encoding` using the shared
  `Assimalign.Cohesion.Http` negotiation primitives (`HttpAcceptParser` /
  `HttpContentNegotiation.TrySelectEncoding`) — the q-value / `identity;q=0` semantics are not
  re-implemented here.
- **Defers the decision to first write.** There is no header-commit hook, so the middleware wraps
  `IHttpResponse.Body`; the media type, status, and any handler-set `Content-Encoding` are only
  known once the handler starts writing. On the first body byte it decides whether to compress,
  stamps `Content-Encoding`, drops the stale `Content-Length`, and always appends
  `Vary: Accept-Encoding` for an eligible media type (never clobbering an existing `Vary` token
  such as the `Accept` a content-negotiated write stamped).
- **Honors a size threshold** without buffering the whole response: it buffers only up to the
  threshold to decide, then streams the remainder through the encoder into the transport's response
  buffer.
- **Never double-compresses** an already-encoded response, and **hands off** cleanly to a handler
  that streams via the response-streaming feature (that path commits its own head and bypasses
  `IHttpResponse.Body`).
- **BREACH-cautious:** over an `https` request the middleware does nothing unless
  `EnableForHttps` is set (default off). "`https`" is the effective scheme, so TLS terminated at a
  trusted proxy counts once `UseForwardedHeaders` runs ahead of this middleware.
- A handler can opt its own response out through `IResponseCompressionFeature.Disable()`.

### Request decompression — `UseRequestDecompression`

- **Transparently inflates** gzip / br / deflate request bodies per `Content-Encoding` before
  handlers read them, by decorating the exchange with a request whose `Body` is the decoded stream
  (the core `IHttpRequest.Body` is get-only and cannot be swapped in place).
- **Zip-bomb guard:** a configurable decompressed-size limit terminates an over-large body with
  `413`. The transport's byte cap protects only the compressed wire bytes; this bounds the decoded
  output.
- **`415`** for an unsupported coding, **`400`** for a malformed coded body; multiple codings
  (`Content-Encoding: gzip, br`) are decoded in reverse application order. A malformed message
  framing under the decoders (a broken chunk size) is the transport's client fault, not the
  content's: it propagates unchanged and the transport answers it (#1340).

## Usage

Configuration lives on the `Add` verbs only ("Add configures, Use places"). Each `Use` verb takes no
arguments: it places the middleware its `Add` verb registered, or one with the defaults when the
application registered none. When the application registers one more than once, the last
registration wins.

Compression size thresholds and decompressed-body limits must be nonnegative; zero is supported.
Assignments below zero throw `ArgumentOutOfRangeException`. `MimeTypes` entries must be
`type/subtype`, `type/*`, or `*/*`; invalid patterns, or both codings disabled, throw
`ArgumentException` from `AddResponseCompression`. Use `*/*` instead of the formerly accepted bare `*`
alias.

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddResponseCompression(compression =>
{
    compression.Options.EnableForHttps = false;              // BREACH: default; enable only when safe
    compression.Options.MinimumResponseSizeBytes = 1024;     // skip tiny responses
    compression.Options.MimeTypes.Add("application/hal+json");
});
builder.Services.AddRequestDecompression(decompression =>
    decompression.Options.MaxDecompressedSizeBytes = 50L * 1024 * 1024);

WebApplication application = builder.Build();
application.UseErrorHandling();

// Response compression — place it early so it wraps the responses it should compress. With output
// caching, place it after UseRouting and UseOutputCache instead (Web.Caching DESIGN, "Ordering").
application.UseResponseCompression();

// Request decompression — after UseErrorHandling, before handlers read the body.
application.UseRequestDecompression();

// Per-response opt-out from a handler:
context.Features.Get<IResponseCompressionFeature>()?.Disable();
```

## Public surface

| Type | Role |
| --- | --- |
| `ResponseCompressionBuilder` | What `builder.Services.AddResponseCompression(compression => ...)` configures: `Options`, and `Build()`, which validates and snapshots them |
| `ResponseCompressionExtensions` | `UseResponseCompression()`, which places the registered middleware |
| `ResponseCompressionOptions` | Codings, eligible media types, size threshold, level, `EnableForHttps` |
| `IResponseCompressionFeature` | Per-exchange opt-out (`Disable()`) read at first write |
| `RequestDecompressionBuilder` | What `builder.Services.AddRequestDecompression(decompression => ...)` configures: `Options`, and `Build()`, which snapshots them |
| `RequestDecompressionExtensions` | `UseRequestDecompression()`, which places the registered middleware |
| `RequestDecompressionOptions` | The decompressed-size guard (`MaxDecompressedSizeBytes`) |

## Diagnostics

Enable the `Assimalign.Cohesion.Web.Compression` EventSource to observe decompression limits and
aborted rejections at Warning, or unsupported and malformed coded bodies at Verbose. The
`decompression-limit-rejections` counter counts each exchange rejected for exceeding the
decompressed-size limit, even when warning events are throttled. Events include the request id and
recognized coding tokens, never raw header values or body content. See [DESIGN.md](DESIGN.md#diagnostics)
for payloads and throttling.

## Dependencies

`Assimalign.Cohesion.Web` (pipeline seams) · `Assimalign.Cohesion.Http` (headers, status codes,
negotiation primitives) · `Assimalign.Cohesion.Http.Forwarded` (the `EffectiveScheme` read behind the
BREACH guard) · `Assimalign.Cohesion.Http.Streaming` (the `HasStarted` probe used on the abort path) ·
`Assimalign.Cohesion.Web.Server` (`IWebClientFaultFeature`, which tells a transport framing failure
from a decoder's, #1340, and `IWebRequestIdFeature` for diagnostic correlation). Per the Web-area
dependency rule it references no hosting module, holds no
DI/configuration/logging state, and is delivered to applications through the `App.Web` shared
framework. Compression itself rides the BCL `GZipStream` / `BrotliStream` / `ZLibStream` — no
external packages.

Design rationale — the deferred-first-write wrapper, the BREACH default, multiple-coding handling,
and the non-goals — lives in [DESIGN.md](DESIGN.md).

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.
