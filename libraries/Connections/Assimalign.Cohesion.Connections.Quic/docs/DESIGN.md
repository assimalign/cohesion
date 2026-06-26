# Assimalign.Cohesion.Connections.Quic — Design

## Design intent

This library is the QUIC **driver** for the Connections contracts in
`Assimalign.Cohesion.Connections`: it adapts `System.Net.Quic` to the
`MultiplexedConnection` / `MultiplexedConnectionListener` /
`MultiplexedConnectionFactory` shapes and nothing more. It carries no
application-protocol semantics — no HTTP/3 framing, no ALPN-specific
behavior switches, no stream-type interpretation. Protocols (HTTP/3 in
`Assimalign.Cohesion.Http.Connections`, and any future QUIC-borne
protocol) consume the contracts by capability and own their own wire
rules.

The one place protocol reality deliberately shapes this driver is
**teardown ordering** (see "Lifecycle and teardown"): how a multiplexed
connection announces its death to the peer is wire-visible and must be
correct for the protocols that run over it.

## Family map

| Type | Visibility | Role |
| --- | --- | --- |
| `QuicConnectionListener` | `public sealed` | Binds a `QuicListener`; `AcceptAsync` yields server-side connections. Created via async `CreateAsync` (binding is inherently async — no constructor). |
| `QuicConnectionFactory` | `public sealed` | Dials outbound connections; `ConnectAsync` yields client-side connections. |
| `QuicConnectionListenerOptions` / `QuicConnectionFactoryOptions` | `public sealed` | Endpoint, TLS/ALPN, stream limits, pipe buffer sizes, default error codes. Both default ALPN to HTTP/3 (see "Error model"). |
| `QuicMultiplexedConnection` | `public sealed` | One QUIC connection; `AcceptStreamAsync` / `OpenStreamAsync` surface streams as `Connection`s and track them for teardown. |
| `QuicStreamConnection` | `internal` | One QUIC stream as a `Connection`: pipes over the stream, direction from the stream's readable/writable halves. |

`QuicMultiplexedConnection` and the listener/factory are `public sealed`
concretes (not interface-first `internal` implementations) because they
*are* the driver's surface: consumers select the driver by constructing
it, then immediately fall back to the `IMultiplexedConnection` /
`IConnection` contracts. This mirrors the Tcp/Udp sibling drivers.

## Stream surfacing

- Direction is derived once at wrap time from `(CanRead, CanWrite)`:
  bidirectional, `ReadOnly` (peer-initiated unidirectional), or
  `WriteOnly` (locally opened unidirectional). A peer cannot open a
  stream that only the remote side writes to, so `OpenStreamAsync`
  rejects `ReadOnly`.
- Unidirectional streams surface honest halves rather than throwing
  lazily: a `WriteOnly` stream's `Input` is a pre-completed reader; a
  `ReadOnly` stream's `Output` is an `UnwritablePipeWriter` whose writes
  throw `InvalidOperationException`.
- Every stream of one connection draws from a **single shared**
  `StreamPipeOptionsContext` (one adaptive memory pool per connection,
  not per stream), owned by the connection and disposed last, after all
  streams and the QUIC connection have released their buffers.
- The stream pipes are created with `leaveOpen: false`, so completing a
  stream's `Output` is the graceful write-side close: the pipe flushes
  remaining bytes and disposes the `QuicStream`, which sends FIN and
  waits for the peer to acknowledge delivery.

## Lifecycle and teardown

Connections are live when produced; there is no separate open step.
Teardown has two paths:

- **`DisposeAsync` (graceful)** — wire-visible ordering is load-bearing:
  1. **Bidirectional streams complete first.** Their write halves carry
     application data (an HTTP/3 response, for example) whose FIN and
     delivery acknowledgement must precede the connection close, or the
     tail of the data could be lost.
  2. **The connection closes** (`CONNECTION_CLOSE` carrying
     `DefaultCloseErrorCode`).
  3. **Unidirectional streams are released only after the close.** In
     multiplexed protocols they are typically long-lived control
     channels; HTTP/3 designates its control and QPACK streams
     *critical* (RFC 9114 §6.2.1, RFC 9204 §4.2), and a peer that
     observes one terminate — including a STOP_SENDING request, which is
     what disposing a partially-read inbound stream emits — before the
     connection close MUST fail the connection with
     `H3_CLOSED_CRITICAL_STREAM`. Disposing them after `CloseAsync`
     releases pipes and handles without putting stream-level frames on
     the wire. This ordering is the driver-level guarantee that lets the
     HTTP/3 layer tear down without a per-stream shutdown protocol.
- **`Abort` (immediate)** — synchronous; fires the connection close
  (fire-and-forget, observing its own faults) and cancels
  `ConnectionClosed`. In-flight data may be discarded; that is the
  contract of abort.

Stream and connection dispose are idempotent, and each stream untracks
itself from the owning connection through a dispose callback, so
connection teardown and individual stream disposal can race safely.
Close failures during teardown (`QuicException` — peer already gone,
`ObjectDisposedException` — abort raced dispose) are swallowed: teardown
must converge, not throw. Pipe completion during stream dispose
likewise absorbs `IOException` (`QuicException` derives from it), which
is routine for streams released after their owning connection closed.

## Error model

- Contract-level failures surface through the area's
  `ConnectionException` family where the contracts demand it; raw
  `QuicException` / `SocketException` pass through on driver-specific
  paths (callers at the protocol layer classify them — see the HTTP/3
  receive loop).
- **Default error codes are the HTTP/3 codes**: `DefaultCloseErrorCode =
  0x100` (`H3_NO_ERROR`) and `DefaultStreamErrorCode = 0x10c`
  (`H3_REQUEST_CANCELLED`), RFC 9114 §8.1. QUIC application error codes
  are meaningless without an application protocol, and both options
  types already default their ALPN list to HTTP/3 — the error codes
  follow the same default so the out-of-the-box configuration is
  self-consistent and RFC-honest on the wire. A listener or factory
  serving a different ALPN protocol overrides the codes alongside
  `ApplicationProtocols`.

## AOT posture

No reflection, no runtime code generation, no serialization. The driver
is `System.Net.Quic` calls plus pipe plumbing from the contracts
library's `Internal` namespace (shared via `InternalsVisibleTo`). Fully
NativeAOT compatible. Platform support follows `System.Net.Quic`
(`windows` / `linux` / `macos`, gated by `QuicListener.IsSupported` at
runtime).

## Non-goals

- **No protocol semantics.** Stream-type prefixes, SETTINGS, GOAWAY,
  and graceful drain belong to the protocol layer. (When the HTTP/3
  layer grows GOAWAY-driven drain, it will run *before* disposal; the
  driver's close ordering stays the final word on the wire.)
- **No per-stream close protocol on connection teardown.** The
  connection close supersedes stream-level signals; see "Lifecycle and
  teardown".
- **No QUIC datagrams, 0-RTT, or connection migration surface.** Not
  exposed by the contracts; add a contract first, then drive it.
- **No transport abstraction re-export.** Consumers depend on
  `Assimalign.Cohesion.Connections` contracts; this package is a driver
  selection.
