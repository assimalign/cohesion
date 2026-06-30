# Assimalign.Cohesion.Connections.Quic — Overview

## Purpose

The QUIC driver for the Cohesion Connections contracts. It adapts
`System.Net.Quic` to `Assimalign.Cohesion.Connections`'
multiplexed-connection shapes so application protocols (HTTP/3, or any
future QUIC-borne protocol) can consume QUIC through the same contracts
as every other transport.

## Scope

- `QuicConnectionListener` — server-side: binds and accepts inbound QUIC
  connections.
- `QuicConnectionFactory` — client-side: dials outbound QUIC connections.
- `QuicMultiplexedConnection` — one QUIC connection; accepts and opens
  streams, each surfaced as a `Connection`.
- Options types covering endpoint, TLS/ALPN, stream limits, pipe buffer
  sizes, and default QUIC application error codes (defaulting to the
  HTTP/3 codes, matching the default ALPN).

Protocol semantics (stream typing, SETTINGS, GOAWAY) are out of scope —
they belong to the protocol layer above. See `DESIGN.md` for the
teardown-ordering guarantee the driver makes to protocols with critical
unidirectional streams.

## Dependencies

- `Assimalign.Cohesion.Connections` — the contracts this driver
  implements, plus shared internal pipe/diagnostics infrastructure via
  `InternalsVisibleTo`.
- `System.Net.Quic` (BCL) — the underlying QUIC implementation; platform
  support follows it (`windows` / `linux` / `macos`, gate with
  `QuicListener.IsSupported`).

## Usage

```csharp
QuicConnectionListener listener = await QuicConnectionListener.CreateAsync(options =>
{
    options.EndPoint = new IPEndPoint(IPAddress.Loopback, 4433);
    options.ServerAuthenticationOptions.ServerCertificate = certificate;
});

IMultiplexedConnection connection = await listener.AcceptAsync(cancellationToken);
IConnection stream = await connection.AcceptStreamAsync(cancellationToken);
```
