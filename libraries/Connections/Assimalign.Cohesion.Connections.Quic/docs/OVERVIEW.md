# Assimalign.Cohesion.Connections.Quic — Overview

## Purpose

The QUIC driver for the Cohesion Connections contracts. It adapts
`System.Net.Quic` to `Assimalign.Cohesion.Connections`'
multiplexed-connection shapes so application protocols (HTTP/3, or any
future QUIC-borne protocol) can consume QUIC through the same contracts
as every other transport.

## Scope

- `QuicConnectionListener` — server-side: constructed from options, then explicitly binds and
  accepts inbound QUIC connections.
- `QuicConnectionFactory` — client-side: dials outbound QUIC connections.
- `QuicMultiplexedConnection` — one QUIC connection; accepts and opens
  streams, each surfaced as a `MultiplexedConnectionStream`, and reports its TLS 1.3
  handshake (ALPN protocol, cipher suite, peer certificate) through the
  `Tls` feature from `Connections.Tls`. It and its streams carry the caller's
  QUIC application error codes through root `Abort(code, reason)` calls: a connection
  close sends `CONNECTION_CLOSE`, and a whole-stream reset sends `STOP_SENDING` and
  `RESET_STREAM`. The `DirectionalAbort` feature still ends one direction alone.
- Options types covering endpoint, TLS/ALPN, stream limits, pipe buffer
  sizes, default QUIC application error codes (defaulting to the
  HTTP/3 codes, matching the default ALPN), and how long disposing a
  connection waits for its streams to deliver what they sent
  (`CloseDeliveryTimeout`). Listener options also expose `HandshakeTimeout`.

Protocol semantics (stream typing, SETTINGS, GOAWAY) are out of scope —
they belong to the protocol layer above. See `DESIGN.md` for the
teardown-ordering guarantee the driver makes to protocols with critical
unidirectional streams.

Streams expose root `IsInputCompleted`: a clean transport end already observed,
without another read. Check the input pipe for buffered bytes as well before treating it as empty.

The connection exposes `connection.StreamLimits.InboundBidirectionalStreamLimit`, a
conservative cumulative upper bound on the peer's bidirectional stream allowance. It grows as
inbound bidirectional streams begin disposal, unlike the fixed concurrent limit configured by
`MaxBidirectionalStreamCount`. Set that option before binding the listener; the bound listener
uses its captured value for both native handshakes and the facet. Retain a local storage cap when
using the facet to validate references, because pending disposals may include unadvertised credit.

## Dependencies

- `Assimalign.Cohesion.Connections` — the contracts this driver
  implements; its pool-owning pipe options are compiled in from that
  library's `shared/` folder (`CohesionSharedSource`). Diagnostics are the
  driver's own internal event source, `Assimalign.Cohesion.Connections.Quic`
  (see DESIGN.md, "Diagnostics").
- `Assimalign.Cohesion.Connections.Tls`, `.StreamAbort` and `.StreamLimits` — optional
  capability contracts and extension properties. Each connection registers its own features;
  streams expose `DirectionalAbort` without inheriting connection TLS or limits.
- `System.Net.Quic` (BCL) — the underlying QUIC implementation; platform
  support follows it (`windows` / `linux` / `macos`, gate with
  `QuicListener.IsSupported`).

## Usage

```csharp
QuicConnectionListenerOptions options = new();
options.EndPoint = new IPEndPoint(IPAddress.Loopback, 4433);
options.ServerAuthenticationOptions.ServerCertificate = certificate;

// Bound each incoming handshake (ten seconds by default). InfiniteTimeSpan disables this timeout.
options.HandshakeTimeout = TimeSpan.FromSeconds(5);

// Disposing a connection waits this long, at most, for its streams' data and FINs to be
// acknowledged before it closes the connection (five seconds by default).
options.CloseDeliveryTimeout = TimeSpan.FromSeconds(2);

await using QuicConnectionListener listener = new(options);
await listener.BindAsync(cancellationToken);

// CreateAsync(options, cancellationToken) remains available as construct-and-bind shorthand.
IMultiplexedConnection connection = await listener.AcceptAsync(cancellationToken);
IMultiplexedConnectionStream stream = await connection.AcceptStreamAsync(cancellationToken);

// End this side gracefully (FIN). The stream stays readable: the peer may keep sending
// until it ends its own side.
await stream.Output.CompleteAsync();
ReadResult rest = await stream.Input.ReadAsync(cancellationToken);

// Or refuse the rest of the peer's data with a code of the protocol's choosing (STOP_SENDING).
// Completing Input without one stops it with DefaultStreamErrorCode. Once both halves are
// complete the QUIC stream is released.
if (stream.DirectionalAbort is { } abort)
{
    abort.AbortRead(0x100); // H3_NO_ERROR
}

await stream.Input.CompleteAsync();

// Or reset the whole stream and end its lifecycle with one root call.
stream.Abort(0x10c); // H3_REQUEST_CANCELLED; already ended directions retain their first signal.
```

QUIC codes range from 0 to 2^62 - 1. The first connection abort or disposal chooses its close
code, including when disposal is still waiting for stream delivery. A later abort interrupts
that wait with the already chosen code, allowing a shorter shutdown budget to force teardown.
The new stream return types
are binary breaks against 10.0.0-preview.1; see DESIGN.md for the migration and accepted breaks.

The driver participates in the common `IConnection` identity, feature, and lifecycle hierarchy.
Listeners expose a stable `Id` and live `Connections`. Entries disappear on closure; disposing
the listener leaves accepted connections open for their owners to dispose. `ConnectionClosed`
fires on abort, observed peer closure, and no later than local disposal.

QUIC connections keep an inbound stream accept pending to observe idle peer closure. Factory
connections grant one concurrent inbound unidirectional stream. A listener configured with
`MaxUnidirectionalStreamCount = 0` also grants one; zero does not forbid unidirectional streams.
The configured bidirectional allowance is unchanged.
