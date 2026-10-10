# Assimalign.Cohesion.Connections.Security

## Summary

TLS for Cohesion connections. Wraps an established `IConnection` (itself an `IDuplexPipe`) in a TLS
session and returns a new, secured `IConnection` whose `Input` / `Output` pipes are encrypted. Lives
in the Connections area because it composes directly over the connection contracts.

## Dependencies

- `Assimalign.Cohesion.Core`
- `Assimalign.Cohesion.Connections`

## Key Types

- `TlsConnectionLayer` — an `IConnectionLayer` that secures connections with TLS (server or client),
  composed onto a listener or factory.
- `TlsConnectionExtensions` — `IConnectionListener.UseTls(TlsServerOptions)` /
  `IConnectionFactory.UseTls(TlsClientOptions)` to compose the layer, and
  `IConnection.UpgradeToTlsAsync(…)` to upgrade a single connection.
- `TlsServerOptions` / `TlsClientOptions` — wrap the BCL `Ssl*AuthenticationOptions` (as
  `AuthenticationOptions`) plus a `HandshakeTimeout`. `TlsServerOptions.RequireClientCertificate` /
  `AllowClientCertificate` set the mutual-TLS policy, with an optional validation callback, and
  `TlsServerOptions.MaxConcurrentHandshakes` (default 512) bounds the handshakes a TLS-layered
  listener runs at once. Each handshake runs on its own task, and one that fails or times out closes
  only its connection.
- The secured connection implements the contracts' `ITlsConnectionInfo`: ALPN protocol, TLS
  version, cipher suite, and the peer's certificate.

## Source Layout

- `src/` — `TlsConnectionLayer` and the options types.
- `src/Extensions` — TLS composition/upgrade extension members on `IConnection`,
  `IConnectionListener`, and `IConnectionFactory`.
- `src/Internal` — the `TlsConnection` decorator backed by `SslStream` over a `DuplexPipeStream`.
