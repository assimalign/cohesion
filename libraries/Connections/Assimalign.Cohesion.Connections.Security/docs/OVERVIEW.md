# Assimalign.Cohesion.Connections.Security

## Summary

TLS for Cohesion connections. Wraps an established `IDuplexConnection` (itself an `IDuplexPipe`) in a TLS
session and returns a new, secured `IDuplexConnection` whose `Input` / `Output` pipes are encrypted. Lives
in the Connections area because it composes directly over the connection contracts.

## Dependencies

- `Assimalign.Cohesion.Core`
- `Assimalign.Cohesion.Connections`
- `Assimalign.Cohesion.Connections.Tls`
- `Assimalign.Cohesion.Connections.StreamAbort`

## Key Types

- `TlsConnectionLayer` — an `IConnectionLayer` that secures connections with TLS (server or client),
  composed onto a listener or factory.
- `TlsConnectionExtensions` — `IDuplexConnectionListener.UseTls(TlsServerOptions)` /
  `IDuplexConnectionFactory.UseTls(TlsClientOptions)` to compose the layer, and
  `IDuplexConnection.UpgradeToTlsAsync(…)` to upgrade a single connection.
- `TlsServerOptions` / `TlsClientOptions` — wrap the BCL `Ssl*AuthenticationOptions` (as
  `AuthenticationOptions`) plus a `HandshakeTimeout`. `TlsServerOptions.RequireClientCertificate` /
  `AllowClientCertificate` set the mutual-TLS policy, with an optional validation callback, and
  `TlsServerOptions.MaxConcurrentHandshakes` (default 512) bounds the handshakes a TLS-layered
  listener runs at once. Each handshake runs on its own task, and one that fails or times out closes
  only its connection.
- The secured connection registers `ITlsConnectionInfo` from `Connections.Tls` under its pinned
  `Tls` name: ALPN protocol, TLS version, cipher suite, and the peer's certificate. Read it with
  `connection.Tls`. Features registered below the TLS layer pass through automatically except
  `StreamAbort`: its inner implementation cannot invalidate plaintext buffered by TLS's outer pipes,
  so `connection.Features.Get<IMultiplexedStreamAbort>()` returns `null`. The TLS layer returns an
  `IDuplexConnection`; the `DirectionalAbort` accessor is available only on `IMultiplexedConnectionStream`.

## Source Layout

- `src/` — `TlsConnectionLayer` and the options types.
- `src/Extensions` — TLS composition/upgrade extension members on `IDuplexConnection`,
  `IDuplexConnectionListener`, and `IDuplexConnectionFactory`.
- `src/Internal` — the `TlsConnection` decorator backed by `SslStream` over a `DuplexPipeStream`.

The driver participates in the common `IConnection` identity, feature, and lifecycle hierarchy.
Listeners expose a stable `Id` and live `Connections`. Entries disappear on closure; disposing
the listener leaves accepted connections open for their owners to dispose. `ConnectionClosed`
fires on abort, observed peer closure, and no later than local disposal.
