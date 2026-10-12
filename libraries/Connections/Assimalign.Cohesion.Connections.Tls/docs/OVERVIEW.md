# Assimalign.Cohesion.Connections.Tls — Overview

Exposes a connection's TLS handshake facts through the optional `Tls` connection feature:
ALPN application protocol, TLS version, cipher suite and remote certificate. The package contains
contracts and accessors; `Connections.Security` runs TLS over byte streams and `Connections.Quic`
reports the TLS handshake QUIC carries itself.

## Usage

Reference `Assimalign.Cohesion.Connections.Tls` and use the family namespace:

```csharp
using Assimalign.Cohesion.Connections;

ITlsConnectionInfo? tls = connection.Tls;
if (tls is not null)
{
    SslApplicationProtocol protocol = tls.ApplicationProtocol;
}
```

`Tls` is available on `IConnection` and `IMultiplexedConnection` and returns `null` when absent.
Multiplexed streams implement `IConnection` and expose only their own features, without inheriting
the parent connection's TLS facts. A driver registers its implementation in `ConfigureFeatures`; its `Name` returns
`ITlsConnectionInfo.FeatureName`, pinned to `"Tls"`. A layer exposes the wrapped collection through
`InnerFeatures`, so its consumers keep seeing TLS facts without forwarding the TLS contract.

## Dependencies and lifetime

The only production reference is `Assimalign.Cohesion.Connections`. The package's namespace is
`Assimalign.Cohesion.Connections`, matching the family pin. The connection owns the remote certificate;
consumers copy it before retaining it beyond the connection's lifetime.

See [DESIGN.md](DESIGN.md) for registration, layering and compatibility.
