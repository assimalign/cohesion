# Assimalign.Cohesion.Web.Compression — Overview

Transparent HTTP body compression for the Cohesion Web pipeline, both directions in one lean
feature package. Cohesion serves HTTP directly in its in-process hosting story, so response
compression (bandwidth, TTFB for JSON/HTML) and safe request decompression cannot be punted to a
reverse proxy. This package fills that gap with two independent, builder-time-configured middleware
verbs over the BCL codecs only — no external dependencies, AOT-safe.

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
  `EnableForHttps` is set (default off).
- A handler can opt its own response out through `IResponseCompressionFeature.Disable()`.

### Request decompression — `UseRequestDecompression`

- **Transparently inflates** gzip / br / deflate request bodies per `Content-Encoding` before
  handlers read them, by decorating the exchange with a request whose `Body` is the decoded stream
  (the core `IHttpRequest.Body` is get-only and cannot be swapped in place).
- **Zip-bomb guard:** a configurable decompressed-size limit terminates an over-large body with
  `413`. The transport's byte cap protects only the compressed wire bytes; this bounds the decoded
  output.
- **`415`** for an unsupported coding, **`400`** for a malformed coded body; multiple codings
  (`Content-Encoding: gzip, br`) are decoded in reverse application order.

## Usage

```csharp
// Response compression — register early so it wraps the responses it should compress.
application.UseResponseCompression(options =>
{
    options.EnableForHttps = false;              // BREACH: default; enable only when safe
    options.MinimumResponseSizeBytes = 1024;     // skip tiny responses
    options.MimeTypes.Add("application/hal+json");
});

// Request decompression — register after UseErrorHandling, before handlers read the body.
application.UseRequestDecompression(options =>
{
    options.MaxDecompressedSizeBytes = 50L * 1024 * 1024;
});

// Per-response opt-out from a handler:
context.Features.Get<IResponseCompressionFeature>()?.Disable();
```

## Public surface

| Type | Role |
| --- | --- |
| `ResponseCompressionExtensions` | `UseResponseCompression(...)` pipeline verb |
| `ResponseCompressionOptions` | Codings, eligible media types, size threshold, level, `EnableForHttps` |
| `IResponseCompressionFeature` | Per-exchange opt-out (`Disable()`) read at first write |
| `RequestDecompressionExtensions` | `UseRequestDecompression(...)` pipeline verb |
| `RequestDecompressionOptions` | The decompressed-size guard (`MaxDecompressedSizeBytes`) |

## Dependencies

`Assimalign.Cohesion.Web` (pipeline seams) · `Assimalign.Cohesion.Http` (headers, status codes,
negotiation primitives) · `Assimalign.Cohesion.Http.Streaming` (the `HasStarted` probe used on the
abort path). Per the Web-area dependency rule it references no hosting module, holds no
DI/configuration/logging state, and is delivered to applications through the `App.Web` shared
framework. Compression itself rides the BCL `GZipStream` / `BrotliStream` / `ZLibStream` — no
external packages.

Design rationale — the deferred-first-write wrapper, the BREACH default, multiple-coding handling,
and the non-goals — lives in [DESIGN.md](DESIGN.md).
