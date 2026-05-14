# Assimalign.Cohesion.Dns.Client.Dot &mdash; Implementation requirements

> **Status: deferred.** This package ships as an empty placeholder
> until the broader Cohesion HTTP / TLS story matures. This document
> is the contract the implementing PR must satisfy.

## Standards

Primary: **[RFC 7858](https://www.rfc-editor.org/rfc/rfc7858)** &mdash;
"Specification for DNS over Transport Layer Security (TLS)".

Supplementary:
- **RFC 1035 §4.2.2** &mdash; DNS over TCP framing (the two-octet
  length-prefix carries over to DoT verbatim).
- **RFC 8310** &mdash; usage profiles + authentication models for DoT
  (Opportunistic, Out-of-band, DANE-authenticated).
- **RFC 7766** &mdash; DNS transport over TCP requirements (idle
  timeouts, EDNS keep-alive interactions).

## Public surface contract

The implementing PR MUST deliver:

```csharp
namespace Assimalign.Cohesion.Dns;

public sealed class DotDnsTransport : DnsTransport
{
    public DotDnsTransport(DotDnsTransportOptions options);

    public override EndPoint Endpoint { get; }

    public override Task<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default);

    protected override ValueTask DisposeAsyncCore();
}

public sealed class DotDnsTransportOptions
{
    public EndPoint Endpoint { get; set; }              // required, defaults to port 853
    public string Host { get; set; }                    // SNI + cert hostname check
    public TimeSpan ConnectTimeout { get; set; }        // default 5s
    public TimeSpan QueryTimeout { get; set; }          // default 5s
    public TimeSpan IdleTimeout { get; set; }           // RFC 7766 idle-close; default 30s

    // Authentication profile (RFC 8310).
    public DotAuthenticationProfile AuthenticationProfile { get; set; }
        // Strict (default) | Opportunistic | Tofu
    public X509Certificate2Collection? PinnedCertificates { get; set; }
    public SslClientAuthenticationOptions? SslOptions { get; set; }
}
```

Plus a factory-builder extension on whatever shape the resolver
builder ends up with:

```csharp
public static IDnsResolverBuilder AddDotDnsTransport(
    this IDnsResolverBuilder builder,
    Action<DotDnsTransportOptions> configure);
```

## Behavior

The transport MUST:

1. Open a TCP connection to `Endpoint` (default port 853) with
   `ConnectTimeout`.
2. Perform a TLS handshake using `SslOptions` (or sensible defaults)
   and validate the server certificate per `AuthenticationProfile`:
   - `Strict`: hostname matches `Host`, chain validates against the
     system trust store. Any failure surfaces as
     `DnsException` with `DnsErrorCode.Transport`.
   - `Opportunistic`: hostname check still applies but a chain
     failure downgrades to encrypted-but-unauthenticated (logs a
     warning rather than throwing). Implementer must surface the
     downgrade through telemetry.
   - `Tofu` (trust-on-first-use): pin the server's
     SubjectPublicKeyInfo after first successful handshake and
     reject subsequent mismatches.
3. Reuse the connection for subsequent `ExchangeAsync` calls until
   `IdleTimeout` elapses, then close gracefully.
4. Use the RFC 1035 §4.2.2 two-octet length prefix for every message.
5. Honour the cancellation token at every async wait.
6. Map every failure to `DnsException`:
   - TLS handshake failure → `DnsErrorCode.Transport`
   - Hostname-mismatch → `DnsErrorCode.Spoofed`
   - Timeout → `DnsErrorCode.Timeout`
   - Truncated response (impossible in TCP framing but worth
     defending against) → `DnsErrorCode.Malformed`

## Dependencies

The package MUST depend on:

- `Assimalign.Cohesion.Dns.Client` (for `DnsTransport` base + resolver
  builder integration).
- Either:
  - `Assimalign.Cohesion.Transports` with a new `IStreamTransform`
    middleware shape that supports TLS wrapping, **or**
  - Direct use of `System.Net.Sockets.TcpClient` +
    `System.Net.Security.SslStream` (the simpler path; preferred
    unless the Transports extension lands first).

The package MUST NOT depend on `Assimalign.Cohesion.Http` &mdash; DoT
runs directly over TLS, not HTTP.

## Test plan

1. **Round-trip against a real DoT resolver** (1.1.1.1 / 9.9.9.9):
   opt-in integration test, gated on a `DnsLiveTests=true` env var.
2. **Hostname-mismatch surfaces `DnsErrorCode.Spoofed`**: use a
   loopback TLS listener with a self-signed cert whose CN doesn't
   match the configured `Host`.
3. **Strict-profile chain failure surfaces `DnsErrorCode.Transport`**:
   self-signed cert + system trust validation.
4. **Connection idle-close round-trips**: send a query, idle past
   `IdleTimeout`, send a second query &mdash; the second open must
   succeed against a re-established connection.
5. **Cancellation mid-handshake**: assert
   `OperationCanceledException` (not `DnsException`) so callers see
   the cancellation cause directly per the standard pattern.
6. **AOT analyzer clean** under
   `<IsAotCompatible>true</IsAotCompatible>`.

## Open questions for the implementer

- **Connection pooling**: one connection per `DotDnsTransport`
  instance, or a pool keyed by `(Host, Endpoint)`? Match whatever
  shape `Assimalign.Cohesion.Transports.TcpClientTransport` adopts
  for keep-alive.
- **EDNS keep-alive (RFC 7828)** integration: should the transport
  ride the resolver's EDNS-option list, or does it manage its own
  RFC 7828 OPT exchange? Decide alongside the resolver
  implementation.
- **TLS 1.3 0-RTT**: out of scope for the first implementation; defer
  to a follow-up story if performance demands it.

## Non-goals

- Acting as a DoT *server*. Server-side authoritative resolution
  lives under the future `Assimalign.Cohesion.Dns.Authority` package
  (Feature `.07` of the L01.01.08 epic, currently deferred).
- Implementing TLS itself. The package consumes `SslStream` (or its
  Transports-library equivalent) and does not own the cipher
  selection or certificate handling beyond the authentication
  profile.
