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
| `QuicConnectionListener` | `public sealed` | Constructed unbound from options; async `BindAsync` acquires the endpoint and `AcceptAsync` yields server-side connections. `CreateAsync` remains the construct-and-bind convenience. |
| `QuicConnectionFactory` | `public sealed` | Dials outbound connections; `ConnectAsync` yields client-side connections. |
| `QuicConnectionListenerOptions` / `QuicConnectionFactoryOptions` | `public sealed` | Endpoint, TLS/ALPN, stream limits, pipe buffer sizes, default error codes. Both default ALPN to HTTP/3 (see "Error model"). |
| `QuicMultiplexedConnection` | `public sealed` | One QUIC connection; `AcceptStreamAsync` / `OpenStreamAsync` surface streams as `Connection`s and track them for teardown. Implements the contracts library's `ITlsConnectionInfo` (see "Handshake facts") and `IMultiplexedConnectionAbort` (see "Application error codes"). |
| `QuicStreamConnection` | `internal` | One QUIC stream as a `Connection`: pipes over the stream, direction from the stream's readable/writable halves. Implements `IMultiplexedStreamAbort`. |

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

## Handshake facts

QUIC runs a TLS 1.3 handshake inside its own (RFC 9001), so a QUIC connection is a TLS-terminating
connection in the contracts' sense. `QuicMultiplexedConnection` implements `ITlsConnectionInfo` and
reports what that handshake negotiated: the ALPN application protocol, the TLS version (always
TLS 1.3), the cipher suite, and the peer's certificate (the client's on a server-side connection,
present when the listener's `ServerAuthenticationOptions` requested one). The values are captured
when the connection is wrapped, after `System.Net.Quic` has completed the handshake, and are the
same on every stream of the connection; the streams themselves do not implement the facet. The
driver reads the values and never branches on them, so it stays free of protocol semantics.

Reading `QuicConnection.RemoteCertificate` hands the certificate's ownership to the reader (a
`QuicConnection` no longer disposes a certificate it has exposed), so `QuicMultiplexedConnection`
disposes the peer certificate after it has disposed the QUIC connection.

## Lifecycle and teardown

Connections are live when produced; there is no separate open step.
Teardown has two paths:

The listener itself has an explicit lifecycle: construction captures configuration without opening a
socket, `BindAsync` asynchronously acquires the endpoint and is idempotent while active, and
`DisposeAsync` releases the endpoint terminally. Restart creates a new listener.

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
  contract of abort. `Abort(Exception?)` closes with
  `DefaultCloseErrorCode`; `Abort(long errorCode, Exception?)`
  (`IMultiplexedConnectionAbort`) closes with the caller's code. The
  first abort or disposal decides the close: `QuicConnection` sends one
  `CONNECTION_CLOSE`, and a later close request has no effect on the wire.

A stream's `ConnectionClosed` fires on its own `Abort` and `DisposeAsync`,
and also when the stream ends underneath it (#1329): the stream watches
`QuicStream.ReadsClosed` and `WritesClosed` and signals when either faults
with anything but `QuicError.OperationAborted`, that is, a peer
`RESET_STREAM` or `STOP_SENDING` (`StreamAborted`) or the loss of the
connection. Faults from this end's own operations (`OperationAborted`)
and a half that ends cleanly signal nothing. The check runs on the thread
pool, never on the QUIC event thread that completed the task. A consumer
learns that the peer abandoned the stream without reading or writing,
which is how an HTTP/3 server fires `RequestCancelled` for a request the
client cancelled.

Stream and connection dispose are idempotent, and each stream untracks
itself from the owning connection through a dispose callback, so
connection teardown and individual stream disposal can race safely.
Close failures during teardown (`QuicException` — peer already gone,
`ObjectDisposedException` — abort raced dispose) are swallowed: teardown
must converge, not throw. Pipe completion during stream dispose
likewise absorbs `IOException` (`QuicException` derives from it), which
is routine for streams released after their owning connection closed.

## Error model

- **A failed inbound handshake never ends the accept (#1304).** `System.Net.Quic` runs each inbound
  connection's handshake in the background and reports one that fails from the next
  `QuicListener.AcceptConnectionAsync`, as an `AuthenticationException` or a `QuicException` (a client
  certificate the policy refuses, a handshake that exceeds its timeout, an error from the
  connection-options callback), while the listener stays usable and the failed connection is already
  disposed. `QuicConnectionListener.AcceptAsync` treats every such exception as that one connection's:
  it reports `HandshakeFailed` (see "Diagnostics") and accepts the next connection, as the contracts'
  `AcceptAsync` requires. Only `ObjectDisposedException` (the listener's disposal, which every later
  accept would report again) and the caller's own cancellation leave `AcceptAsync`. Before #1304 the
  exception left `AcceptAsync`, so a single client without a required certificate stopped the HTTP/3
  endpoint and, through the HTTP listener's fatal accept handling, every other endpoint of the server.
  Kestrel's QUIC transport makes the same call.
- **Pending handshakes are bounded by `Backlog`.** It becomes `QuicListenerOptions.ListenBacklog`,
  which counts connections whose handshake is in progress plus those waiting to be accepted;
  `System.Net.Quic` refuses new connections beyond it. The handshake timeout is
  `System.Net.Quic`'s default (10 seconds). `TlsServerOptions.HandshakeTimeout` and
  `MaxConcurrentHandshakes` belong to the TCP TLS layer and do not apply here.
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
  `ApplicationProtocols`. The defaults apply only where the caller gives
  no code (see "Application error codes"): the stream default on a
  stream's `Abort(Exception?)`, its disposal, and the completion of its
  `Input` or `Output`; the close default on the connection's
  `Abort(Exception?)` and disposal. The options' XML docs say the same.

## Application error codes

The driver implements the contracts library's two code-carrying facets
(#1080), so a protocol chooses the code each abort puts on the wire:

| Call | `System.Net.Quic` call | Frame |
| --- | --- | --- |
| stream `AbortRead(errorCode)` | `QuicStream.Abort(QuicAbortDirection.Read, errorCode)` | `STOP_SENDING` |
| stream `AbortWrite(errorCode)` | `QuicStream.Abort(QuicAbortDirection.Write, errorCode)` | `RESET_STREAM` |
| connection `Abort(errorCode, reason)` | `QuicConnection.CloseAsync(errorCode)` | `CONNECTION_CLOSE` |

- **A direction ends once.** `QuicStream.Abort` skips a direction that
  has already ended (read to its end, completed, or aborted), so a stream
  `Abort(Exception?)` or disposal after a coded abort sends the default
  code only for a direction still open. That is how the HTTP/3 transport
  resets with a code: both directions, then `Abort(reason)`.
- **A half abort is not the stream ending.** It changes no `State` and
  does not fire the stream's `ConnectionClosed`: the `ReadsClosed` or
  `WritesClosed` fault it causes is `OperationAborted`, which the
  peer-closure watch ignores (see "Lifecycle and teardown").
- **After `AbortRead`, every read fails.** The contract says so, and the
  in-memory driver does it, but the pipe `PipeReader.Create` builds over
  the `QuicStream` returns octets it has buffered and the holder has not
  examined without reading the stream. So a readable stream's `Input` is
  a thin delegating reader that checks a flag `AbortRead` sets before the
  stream is aborted, and fails `ReadAsync`, `TryRead`, and
  `ReadAtLeastAsync` with `QuicException(OperationAborted)`, the error
  `QuicStream` gives a read the abort overtakes. A read already waiting
  on the stream fails in `QuicStream` itself. Everything else passes
  through, so a read costs one flag check.
- **Aborting a stream that has ended does nothing.** A disposed stream,
  or one whose connection is gone, has nothing to tell the peer, so the
  driver swallows `ObjectDisposedException` and `QuicException` there. A
  code outside 0 to 2^62 - 1 throws `ArgumentOutOfRangeException` before
  the stream is touched.
- **Abort the read direction before completing `Output` on an unread
  stream.** Completing either pipe disposes the `QuicStream`
  (`leaveOpen: false`), and the disposal stops a read direction that is
  still open with `DefaultStreamErrorCode`. A protocol that ends its
  sending direction while the peer is still sending therefore calls
  `AbortRead` with its own code first. #1330 separates the two
  directions, so that completing `Output` sends the FIN and leaves
  reading open.

## Diagnostics

The driver reports through its own internal event source, named for the assembly:
`Assimalign.Cohesion.Connections.Quic` (`Internal/EventSource/QuicConnectionEventSource.cs`), per the
repository EventSource convention (`.claude/rules/event-source.md`). Tools enable it by name
(`dotnet-trace`, `dotnet-counters`); an application forwards it into its logging with
`Assimalign.Cohesion.Logging.EventSource`, where the source name becomes the log category.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | `ListenerBound` | Informational | `listenerId`, `endPoint` |
| 2 | `ListenerClosed` | Informational | `listenerId` |
| 3 | `ConnectionOpened` | Informational | `connectionId`, `listenerId` (empty when dialed), `localEndPoint`, `remoteEndPoint` |
| 4 | `ConnectionClosed` | Informational | `connectionId` |
| 5 | `StreamOpened` | Verbose | `streamId`, `connectionId`, `direction` |
| 6 | `StreamClosed` | Verbose | `streamId` |
| 7 | `HandshakeFailed` | Warning | `listenerId`, `exceptionType`, `exceptionMessage` — an inbound connection whose handshake failed, which the listener dropped |

Counters: `current-connections`, `total-connections`, `connections-per-second`, `current-streams`,
and `streams-per-second`.

- Streams report at Verbose and apart from connections: HTTP/3 opens one per request, and folding them
  into the connection events (as the retired shared forwarder did) made every stream look like a
  connection.
- `Abort` and `DisposeAsync` both end a connection or stream; whichever runs first reports the close,
  once, behind an `Interlocked` flag, so the `current-*` gauges stay exact when an aborted object is
  disposed later — or never.
- Lifetimes use `Opened`/`Closed`/`Bound` names rather than `Start`/`Stop`: they begin and end on
  different async flows, which EventSource's activity tracking would mis-nest.
- TLS handshake events come from the runtime's own `System.Net.Security` and `System.Net.Quic`
  sources; forward them by adding those prefixes to the forwarder.
- `HandshakeFailed` is the one handshake event this driver raises, because dropping the connection
  is its decision. It is a warning (the connection is lost, the listener recovered) and carries the
  exception's type and message, never a certificate or key material. A dropped connection never
  reports `ConnectionOpened`: `System.Net.Quic` disposed it before this driver wrapped it.

## AOT posture

No reflection, no runtime code generation, no serialization. The driver
is `System.Net.Quic` calls plus pipe plumbing from the contracts
library's `shared/` folder - `PipeOptionsFactory` and `StreamPipeOptionsContext` are compiled
into this driver as internal types - with lifecycle reporting through its own
internal event source (see "Diagnostics"). Fully
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
