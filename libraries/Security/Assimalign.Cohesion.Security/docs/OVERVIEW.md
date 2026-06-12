# Assimalign.Cohesion.Security

## Summary

TLS connection-upgrade for Cohesion connections. Wraps an established `IConnection` in a TLS
session and returns a new, secured `IConnection` whose transport is encrypted.

## Dependencies

- `Assimalign.Cohesion.Core`
- `Assimalign.Cohesion.Connections`

## Key Types

- `TlsConnectionExtensions` — `UpgradeToTlsAsync(TlsServerOptions)` / `UpgradeToTlsAsync(TlsClientOptions)` on `IConnection`.
- `TlsServerOptions` / `TlsClientOptions` — wrap the BCL `Ssl*AuthenticationOptions` plus a handshake timeout.

## Source Layout

- `src/` — options types.
- `src/Extensions` — `IConnection` TLS-upgrade extension members.
- `src/Internal` — `TlsConnection` decorator and the pipe/stream adapters that back `SslStream`.
