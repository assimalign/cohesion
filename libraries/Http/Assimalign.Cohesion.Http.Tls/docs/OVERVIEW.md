# Assimalign.Cohesion.Http.Tls — Overview

Gives handlers the TLS session of the connection an exchange arrived on, as a feature on
`IHttpContext`: the client certificate, the TLS protocol version, the cipher suite, and the
application protocol ALPN selected.

## Scope

- `IHttpTlsConnectionFeature`, the session contract.
- `context.TlsConnection`, which returns the session or `null` for a cleartext exchange. It works
  the same on HTTP/1.1, HTTP/2, and HTTP/3.
- An override point: a feature a middleware installs in `context.Features` is returned instead of
  the connection's own session.

## Usage

```csharp
using Assimalign.Cohesion.Http;

if (context.TlsConnection is { ClientCertificate: { } certificate } tls)
{
    // The handshake asked for a certificate and the client sent one. The connection owns it,
    // so copy what you keep beyond the exchange.
    string subject = certificate.Subject;
    SslProtocols protocol = tls.Protocol;
}
```

Whether a client certificate is requested, required, and how it is validated is configured on the
endpoint's TLS options (`TlsServerOptions.RequireClientCertificate` or `AllowClientCertificate` in
`Assimalign.Cohesion.Connections.Security`).

## Dependencies

- `Assimalign.Cohesion.Http`, the protocol core: `IHttpContext` and the feature collection.
- `Assimalign.Cohesion.Connections`, the contracts library: `ITlsConnectionInfo`, the handshake facet.

The server transport (`Assimalign.Cohesion.Http.Connections`) does not reference this package. It
publishes the handshake as the `ITlsConnectionInfo` facet of each exchange's `ConnectionInfo`, and
the accessor builds the feature from that facet on first read. A request-parse interceptor, which
runs before the exchange exists, reads the facet directly from its context's `ConnectionInfo`. The
`App.Web` shared framework delivers this package to web applications.

## Non-goals

No TLS configuration, no certificate validation, and no authentication from the certificate. See
[DESIGN.md](./DESIGN.md).
