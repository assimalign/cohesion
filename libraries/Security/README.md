# Security

Security building blocks for the Cohesion stack: transport-layer security as a composable
connection transformation, and certificate management.

## Projects

| Project | Role |
|---|---|
| `Assimalign.Cohesion.Security` | TLS as a connection upgrade: `UseTls(...)` on listeners/factories (via `TlsConnectionLayer : IConnectionLayer`) and `UpgradeToTlsAsync(...)` on individual connections. Returns a new secured `IConnection` whose duplex pipe is encrypted and whose `Capabilities.Security` reports `Tls`. |
| `Assimalign.Cohesion.Security.Cryptography` | Certificate store management abstractions and OS-specific certificate providers. |

## Layering

`Assimalign.Cohesion.Security` sits directly above the connection layer: it depends on
`Assimalign.Cohesion.Connections` and transforms connections without the transports or the
application protocols knowing about it. Application protocols observe security only through
`ConnectionCapabilities.Security`. Certificates for the TLS options can be sourced via
`Assimalign.Cohesion.Security.Cryptography`.

## Dependencies

- `Assimalign.Cohesion.Core`
- `Assimalign.Cohesion.Connections` (Security only)

## Further Reading

- [Assimalign.Cohesion.Security/docs/OVERVIEW.md](Assimalign.Cohesion.Security/docs/OVERVIEW.md)
- [Assimalign.Cohesion.Security/docs/DESIGN.md](Assimalign.Cohesion.Security/docs/DESIGN.md) —
  why TLS is an upgrade returning a new connection rather than middleware observing an existing one.
- [Assimalign.Cohesion.Security.Cryptography/docs/OVERVIEW.md](Assimalign.Cohesion.Security.Cryptography/docs/OVERVIEW.md)
