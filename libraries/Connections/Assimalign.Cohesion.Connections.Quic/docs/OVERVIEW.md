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
  streams, each surfaced as a `Connection`, and reports its TLS 1.3
  handshake (ALPN protocol, cipher suite, peer certificate) through the
  contracts' `ITlsConnectionInfo`. It and its streams carry the caller's
  QUIC application error codes through the contracts'
  `IMultiplexedConnectionAbort` (`CONNECTION_CLOSE`) and
  `IMultiplexedStreamAbort` (`STOP_SENDING`, `RESET_STREAM`).
- Options types covering endpoint, TLS/ALPN, stream limits, pipe buffer
  sizes, default QUIC application error codes (defaulting to the
  HTTP/3 codes, matching the default ALPN), and how long disposing a
  connection waits for its streams to deliver what they sent
  (`CloseDeliveryTimeout`).

Protocol semantics (stream typing, SETTINGS, GOAWAY) are out of scope —
they belong to the protocol layer above. See `DESIGN.md` for the
teardown-ordering guarantee the driver makes to protocols with critical
unidirectional streams.

## Dependencies

- `Assimalign.Cohesion.Connections` — the contracts this driver
  implements; its pool-owning pipe options are compiled in from that
  library's `shared/` folder (`CohesionSharedSource`). Diagnostics are the
  driver's own internal event source, `Assimalign.Cohesion.Connections.Quic`
  (see DESIGN.md, "Diagnostics").
- `System.Net.Quic` (BCL) — the underlying QUIC implementation; platform
  support follows it (`windows` / `linux` / `macos`, gate with
  `QuicListener.IsSupported`).

## Usage

```csharp
QuicConnectionListenerOptions options = new();
options.EndPoint = new IPEndPoint(IPAddress.Loopback, 4433);
options.ServerAuthenticationOptions.ServerCertificate = certificate;

// Disposing a connection waits this long, at most, for its streams' data and FINs to be
// acknowledged before it closes the connection (five seconds by default).
options.CloseDeliveryTimeout = TimeSpan.FromSeconds(2);

await using QuicConnectionListener listener = new(options);
await listener.BindAsync(cancellationToken);

// CreateAsync(options, cancellationToken) remains available as construct-and-bind shorthand.
IMultiplexedConnection connection = await listener.AcceptAsync(cancellationToken);
IConnection stream = await connection.AcceptStreamAsync(cancellationToken);

// End this side gracefully (FIN). The stream stays readable: the peer may keep sending
// until it ends its own side.
await stream.Output.CompleteAsync();
ReadResult rest = await stream.Input.ReadAsync(cancellationToken);

// Or refuse the rest of the peer's data with a code of the protocol's choosing (STOP_SENDING).
// Completing Input without one stops it with DefaultStreamErrorCode. Once both halves are
// complete the QUIC stream is released.
if (stream is IMultiplexedStreamAbort abort)
{
    abort.AbortRead(0x100); // H3_NO_ERROR
}

await stream.Input.CompleteAsync();
```
