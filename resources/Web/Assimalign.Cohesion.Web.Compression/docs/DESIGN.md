# Assimalign.Cohesion.Web.Compression — Design

## Design intent

Two directions of transparent body compression, packaged together (no ASP.NET-style micro-package
split) as plain Web feature middleware under the middleware-first composition model. Each direction
is one `Use*` verb, options captured at builder time, no service container, no request-time service
location, no transport changes. Compression rides the BCL codecs only (`GZipStream`, `BrotliStream`,
`ZLibStream`), so the package is AOT/trim-clean with no external dependencies.

The correctness of the response half hinges on the response-body write path and header-commit
timing, which is why it was sequenced behind the write-path maturity (#762, the streaming seam). The
mechanics that shape this design were verified against the transports before building; they are
recorded below because they are not obvious from the abstractions.

## The response write path this builds on (verified mechanics)

- `IHttpResponse.Body` defaults to a **buffered `MemoryStream`** (`TransportHttpResponse`). A
  handler writing to it accumulates bytes in memory; nothing reaches the wire until the exchange
  ends. `Body` has a public setter, so a middleware may replace it.
- The server loop runs the **entire pipeline first**, then calls the transport's `SendAsync`. Only
  inside `SendAsync` does the transport read the buffered body, **synthesize `Content-Length` from
  the final buffer length if and only if the header is absent**, and commit the head. All three
  transports (HTTP/1.1, HTTP/2, HTTP/3) follow this shape.
- The response-streaming feature (`IHttpResponseStreamingFeature`, `Http.Streaming`) writes to a
  **separate** transport sink, not to `IHttpResponse.Body`. When that sink has started, `SendAsync`
  short-circuits and never reads `IHttpResponse.Body` at all.

Three consequences drive the design: (1) a middleware can freely set `Content-Encoding` and remove
`Content-Length` any time before the pipeline returns, and the transport will re-synthesize the
length from the compressed bytes; (2) wrapping `IHttpResponse.Body` reaches every handler on the
buffered path; (3) wrapping `IHttpResponse.Body` does **not** reach a handler that streams through
the sink — that path must be handed off, not corrupted.

## The deferred-first-write wrapper

There is no `OnStarting` hook and no header-commit callback a middleware can register to decide
compression once the response shape is known. The media type, the status, and any handler-set
`Content-Encoding` only exist once the handler starts writing the body. So the middleware wraps
`IHttpResponse.Body` in `CompressionBodyStream` and **defers the whole decision to the first
write**:

1. **At entry** it negotiates the coding from `Accept-Encoding` (via the shared Http primitive) and
   installs the wrapper and the opt-out feature. No headers are touched yet.
2. **On the first body write** it evaluates eligibility against the now-known headers — handler
   opt-out, an already-present `Content-Encoding`, a bodyless status, then the media-type gate. For
   an eligible media type it stamps `Vary: Accept-Encoding`. If the client accepts one of our
   codings it enters a **buffering** state; otherwise it passes through as identity (with `Vary`
   already stamped).
3. **The size threshold** is applied without buffering the whole response: while buffering it holds
   at most `MinimumResponseSizeBytes` bytes. If the body crosses the threshold it **engages the
   encoder** — sets `Content-Encoding`, removes `Content-Length`, and streams the buffered bytes
   plus everything after through a `GZipStream`/`BrotliStream` into the original buffer. If the body
   completes at or below the threshold it flushes the buffered bytes uncompressed.
4. **After the pipeline returns**, the middleware calls `CompleteAsync` (in a `finally`) which
   flushes and closes the encoder so its trailing block lands in the buffer, then restores the
   original body for the transport to read. The transport re-synthesizes `Content-Length` from the
   compressed length because the stale one was removed.

The encoder streams into the transport's *existing* response buffer, so the wrapper never
accumulates a second full copy of the body — it holds at most one threshold's worth while undecided.
Finalization uses `CancellationToken.None`: the encoder trailer must land in the buffer even when
the request was aborted, and the writes target an in-memory buffer (no I/O to cancel). The transport
decides separately whether to actually send.

### Composition with streamed responses (handoff, not compression)

A handler that writes through `context.Response.Streaming` commits its own head on the first write
and bypasses `IHttpResponse.Body` entirely. The compression wrapper on `IHttpResponse.Body` is
simply never written to, and `SendAsync` short-circuits to the sink — so the streamed response is
**left untouched**, never corrupted. Compressing the streaming-sink path would require a transport
response interceptor wired at server-composition time, which crosses the Web-area hosting-isolation
boundary; it is a deliberate non-goal (see below). "Composes with the streaming write path" here
means *does not break it*.

### Composition with the exception boundary (#864/#881)

The wrapper preserves the no-clobber/`HasStarted` contract other middleware rely on: it does not
commit the head itself (the transport still does, from the buffered body), so a fault after the
handler wrote a partial body is handled by the exception boundary exactly as before — an unstarted
buffered response is reset and rewritten. When the exception boundary resets the response it clears
the headers (including any `Content-Encoding`/`Vary` the wrapper staged) and the wrapper's
`CompleteAsync` then finalizes over the reset body harmlessly.

## Vary: always append, never clobber

The representation depends on `Accept-Encoding`, so every response for an eligible media type
carries `Vary: Accept-Encoding` — including the identity one a non-accepting client receives — so a
shared cache never serves a coded body to a client that did not accept it. The token is **appended**
to any existing `Vary` (preserving, for example, the `Accept` a content-negotiated write stamped)
and never duplicated or allowed to override a `Vary: *`. This mirrors the append helper in
`Web.Serialization` and `Web.StaticFiles`. When compression is skipped for an ineligible media type,
or entirely over HTTPS by the BREACH default, no `Vary` is added — those responses do not vary by
`Accept-Encoding`.

## identity and unacceptable-encoding handling (RFC 9110 §12.5.3)

The `Accept-Encoding` q-value logic lives entirely in `HttpContentNegotiation.TrySelectEncoding`; it
is not re-implemented here. Two edges are handled through it:

- **No `Accept-Encoding` header** — the primitive treats a missing field as *identity only* (the
  conservative real-server behavior: compress only when the client explicitly asks). Such a response
  is served uncompressed, with `Vary` still stamped for the eligible media type.
- **`identity;q=0`** — when the client refuses the uncompressed representation, the size threshold
  cannot fall back to identity, so an eligible response is compressed **from the first byte
  regardless of size**. This acceptability is obtained by re-using the same primitive with an empty
  server-coding list (which returns whether identity is acceptable).
- **Nothing acceptable (including identity)** — the middleware *disregards* the impossible
  constraint and serves identity rather than answering `406`, matching the `Web.StaticFiles`
  choice: refusing to serve a representation over a hostile or misconfigured header helps no one.
  This is a deliberate choice among the two RFC-permitted options (406 or disregard).

## BREACH (CVE-2013-3587)

Compressing attacker-influenced dynamic content alongside a secret over TLS can leak the secret
through compressed-length observation (BREACH). So `EnableForHttps` defaults to **false**: over an
`https` request the middleware does nothing — no wrapper, no `Vary`, no compression. The scheme is
known at entry, so the default path skips HTTPS responses with zero per-response overhead. Enabling
the flag is an explicit, documented opt-in for pipelines whose responses do not mix a secret with
reflected input. Compression over HTTP (for example behind a TLS-terminating proxy where the app
sees `http`) is on by default.

## Request decompression

`IHttpRequest.Body` is get-only and cannot be swapped in place, so transparent decompression is
delivered by **decorating the exchange**: `RequestDecompressionHttpContext` forwards everything to
the real context except `Request`, whose `Body` is the decoded stream. This mirrors the
context-decoration pattern `Web.RequestTimeouts` uses for the cancellation token. Once decoding is in
place the request's `Content-Encoding` and `Content-Length` are removed, since downstream reads the
decoded identity representation and the compressed length no longer describes it.

### Multiple codings

`Content-Encoding: gzip, br` means the origin applied gzip then br, so the wire is `br(gzip(body))`.
The chain is decoded in **reverse application order**: the innermost decoder wraps the transport body
(and leaves it open — the transport owns it), each outer decoder closes the one it wraps, so
disposing the final stream cascades the whole chain. If **any** listed coding is unsupported the
whole request is refused with `415` before a handler runs. `deflate` is interpreted as the RFC 9110
§8.4.1.2 zlib data format (`ZLibStream`); a bare raw-deflate body is a documented non-goal.

### The zip-bomb guard, applied lazily

The body is decoded lazily as the handler reads it — nothing is inflated up front. A
`LimitedDecompressionStream` counts the decoded bytes and throws once the running total exceeds
`MaxDecompressedSizeBytes`. Because each decoder in a chain pulls only what the layer above consumes,
bounding the final read also bounds the work and memory of every intermediate layer, so the
top-level cap is sufficient even for a nested chain. The guard trip surfaces as an exception during
the handler's read, which the middleware catches and turns into `413`; a decoder rejecting a
malformed body surfaces (through a typed wrapper) as `400`.

### Ordering requirement

Because the guard surfaces during the handler's body read, `UseRequestDecompression` must be
registered **after** `UseErrorHandling`: the exception boundary must sit *outside* it so that,
between this middleware and the handler, there is no catch-all to intercept the guard's exception
before this middleware's own catch converts it to a clean `413`/`400`. Error handling is
conventionally the outermost middleware, so this is the natural order.

## Error model

- `413 Content Too Large` — decompressed request body exceeded the guard (internal
  `RequestDecompressionLimitException`, never escapes the package).
- `415 Unsupported Media Type` — an unsupported request content coding (decided before any handler
  runs).
- `400 Bad Request` — a malformed coded request body (internal `RequestDecompressionFormatException`
  wrapping the decoder's `InvalidDataException`, so an unrelated handler `InvalidDataException` is
  never mis-mapped).
- On any of these, a response that has already started streaming is aborted at the protocol layer
  (`IHttpContext.CancelAsync`) instead, since its head is locked.

## AOT posture

No reflection anywhere: negotiation is the span-based Http primitive, the media-type matcher is a
precompiled hash-set lookup, codings are string constants, and compression rides the BCL codecs.
Nothing in the package or its tests needs dynamic code. `IsAotCompatible=true` holds.

## Non-goals

- **No zstd.** The BCL ships no zstd codec; adding one would mean an external dependency, which the
  package deliberately avoids. gzip and Brotli cover the negotiated web-client set.
- **No dictionary / shared-Brotli transports** (compression dictionaries, `Available-Dictionary`).
  Out of scope for a first body-compression pass.
- **No compression of the streaming-sink path.** A handler that streams via
  `IHttpResponseStreamingFeature` is handed off untouched; compressing it needs a transport
  interceptor across the hosting-isolation boundary.
- **No raw-deflate (RFC 1951) request bodies.** `deflate` is the RFC 9110 zlib format; a bare-deflate
  sender gets a `400`.
- **No HEAD length mirroring.** A bodyless HEAD is left alone; precisely mirroring the coded
  `Content-Length` a GET would produce is a separate transport concern.
- **No per-endpoint metadata surface.** Compression is a pipeline-wide policy configured at builder
  time; a handler opts its own response out through `IResponseCompressionFeature`, and finer-grained
  per-route control belongs to whatever source-generated mapping layer arrives later.
- **No Microsoft.Extensions dependencies, no separate hosting wiring.** The package composes purely
  against the Web root's builder seams and ships to applications via `App.Web`.
