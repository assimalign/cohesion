# Assimalign.Cohesion.Http.ExtendedConnect — Design

## Purpose

Model the HTTP/2 and HTTP/3 *extended CONNECT* mechanism (RFC 8441, RFC 9220) as
an explicit, opt-in feature on `IHttpContext`, so an application can detect that
an exchange is an extended CONNECT and read the requested protocol — without the
transport layer taking a dependency on this package.

Extended CONNECT is a `CONNECT` request that additionally carries the
`:protocol` pseudo-header. It is the mechanism a client uses to bootstrap another
protocol (most commonly WebSocket, `:protocol = websocket`) over a single HTTP/2
or HTTP/3 stream.

## Shape

- `IHttpExtendedConnectFeature` — the public contract. Exposes only `Protocol`
  (the requested `:protocol` value). Inherits `IHttpFeature`.
- `HttpExtendedConnectFeature` (internal) — the default implementation, a thin
  immutable holder of the protocol string.
- `HttpExtendedConnectExtensions` — `IHttpContext.ExtendedConnect` (returns the
  feature, or `null`) and `IHttpContext.IsExtendedConnect` (a convenience bool).

The package surfaces its types under the `Assimalign.Cohesion.Http` namespace
(not the assembly name) so the `IHttpContext` extension members are discoverable
without an extra `using`. This matches the sibling `Http.ProtocolUpgrade` package
and is recorded as a deliberate deviation in the csproj.

## How the value arrives — the transport bridge

The HTTP/2 (`Http2Stream`) and HTTP/3 (`Http3HeaderCodec` / `Http3ConnectionContext`)
transports recognize the `:protocol` pseudo-header and, **only for a valid
extended CONNECT**, stash it verbatim under a well-known `IHttpContext.Items`
key (`":protocol"`, the pseudo-header name by convention — the transport's
internal `TransportItemKeys.Protocol`). This package reads that same key.

The deliberate consequence: the transport produces a loosely-typed string and
knows nothing about this package; this package interprets that string into a
strongly-typed feature. The two agree only on the literal key, so there is no
compile-time coupling and no shared symbol to keep in sync.

## Validation lives in the transport, not here

Whether a request *is* a valid extended CONNECT is decided in the transport,
using the shared `HttpFieldNormalization.ValidateExtendedConnect` rule (RFC 8441
§4 / RFC 9220 §3): `:protocol` is only valid on `CONNECT`, and an extended
CONNECT MUST also carry `:scheme`, `:path`, and `:authority`. A malformed
extended CONNECT is rejected at the wire layer (HTTP/2: connection-level
`PROTOCOL_ERROR` / GOAWAY; HTTP/3: the offending request stream is dropped) and
therefore never reaches this package. Consequently, when `ExtendedConnect` is
non-`null`, `Protocol` is guaranteed non-empty and the request was well-formed.

## Non-goals

- **No WebSocket framing surface.** This package recognizes and models extended
  CONNECT so an application can detect it and respond deterministically; it does
  **not** ship a WebSocket (RFC 6455) framing/codec layer or surrender a tunnel
  stream. Bootstrapping the inner protocol is the application's concern.
- **No client-side initiation.** This is the server-side recognition surface;
  it does not build extended CONNECT requests.

## AOT posture

Pure managed code with no reflection, dynamic code generation, or runtime type
inspection. The feature lookup is a dictionary read and a type check, so the
package is trimming- and NativeAOT-safe.
