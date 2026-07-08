# Assimalign.Cohesion.Http.InterimResponses — Design

Interim (`1xx`) responses are a response-side capability, not protocol machinery,
so they ship as their own feature package that plugs into the transport through
the generic `IHttpResponseInterceptor` seam — the same way
`Assimalign.Cohesion.Http.Streaming` plugs into response writing and
`Assimalign.Cohesion.Http.ProtocolUpgrade` plugs into connection takeover. Neither
the protocol core nor the transport (`Assimalign.Cohesion.Http.Connections`)
depends on this package.

## Why a separate package

An earlier revision baked an `IHttpInterimResponseFeature` into the protocol core
(`Assimalign.Cohesion.Http`) and had the transport install it on every exchange.
That coupled three layers to one capability and broke the repository convention
that HTTP features live in their own packages behind the interceptor seam. This
package removes that coupling, mirroring the streaming/upgrade split:

- **The core** owns only the generic `IHttpResponseInterceptor` seam and a small
  transport-capability contract, `IHttpInterimResponseWriter` (a peer of
  `IHttpConnectionTakeover`), surfaced on `HttpResponseInterceptorContext`. It has
  no interim *feature* type.
- **The transport** owns the per-version wire emission — an HTTP/1.1 status line,
  an HTTP/2 HEADERS block, an HTTP/3 QPACK field section, each with no body and no
  `Content-Length` — behind an internal `IHttpInterimResponseWriter` implementation
  it exposes through the interceptor context. It has no interim *feature* type.
- **This package** owns the typed `IHttpInterimResponseFeature`, the interceptor
  that installs it, and the ergonomic accessors. It depends only on the core.

Interim responses are therefore **opt-in**: a host enables them by registering
`HttpInterimResponses.CreateInterceptor()` on the transport's response
interceptors. Where it is not registered, the transport uses its normal response
path with zero interim overhead (the capability is created only when at least one
response interceptor is registered).

## How it works

1. A host adds `HttpInterimResponses.CreateInterceptor()` to
   `HttpConnectionListenerOptions.ResponseInterceptors`.
2. Per exchange, the transport creates its per-protocol interim-response writer and
   runs the registered response interceptors, exposing it as
   `HttpResponseInterceptorContext.InterimResponseWriter`.
3. `HttpInterimResponseInterceptor.OnResponse` wraps that capability in a
   `HttpInterimResponseFeature` and installs it on `context.Features` (only when
   the capability is present).
4. The handler resolves it — `context.InterimResponse`,
   `context.SendEarlyHintsAsync(...)`, or `context.SendContinueAsync()` — and emits
   one or more interim responses ahead of the final response.

## Contract and boundaries

- **`IsInterimResponseSupported`** reflects the transport capability's
  `CanWriteInterimResponse`: `true` while the final response has not started, then
  `false` once the head is committed (a streamed body started, or an HTTP/1.1
  connection was taken over). It is the report-don't-throw discoverability path —
  a caller checks it and never provokes an exception for an unsupported state.
- **`SendInterimResponseAsync`** forwards to the capability, which validates the
  status (`1xx`, not `101` — `101 Switching Protocols` belongs to
  `Http.ProtocolUpgrade`) and rejects an emission after the final response has
  started. The convenience `SendEarlyHintsAsync` / `SendContinueAsync` extensions
  no-op (return `false`) when the feature is absent or unsupported, so a handler
  can call them unconditionally.
- Interim responses carry no body; the transport writes no `Content-Length`. HTTP/2
  and HTTP/3 peers may receive several before the final response.

## Not in scope here

- **The automatic `Expect: 100-continue` handshake.** That is a wire-level interop
  concern the HTTP/1.1 transport handles unconditionally (it solicits the body with
  `100 Continue` before reading it, so a withholding client does not deadlock),
  independent of this package. See the transport's `docs/DESIGN.md`.
- **The `1xx`-as-final-status guard.** Rejecting a `1xx` set as the final
  `Response.StatusCode` is enforced in the transport's response write paths, not
  here.
- **Per-version wire framing.** Owned by the transport behind the capability.

## AOT posture

No reflection or runtime code generation. The feature is a thin forwarder over the
transport capability; the extensions build a small `HttpHeaderCollection` for the
`Link` fields. Builds clean under the trim/AOT analyzers.
