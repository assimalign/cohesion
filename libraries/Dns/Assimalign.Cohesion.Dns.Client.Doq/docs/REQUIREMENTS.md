# Assimalign.Cohesion.Dns.Client.Doq &mdash; Implementation requirements

> **Status: deferred.** This package ships as an empty placeholder
> until `Assimalign.Cohesion.Transports` ships a usable QUIC client
> transport. This document is the contract the implementing PR
> must satisfy.

## Standards

Primary: **[RFC 9250](https://www.rfc-editor.org/rfc/rfc9250)** &mdash;
"DNS over Dedicated QUIC Connections".

Supplementary:
- **RFC 9000** &mdash; QUIC v1 (the underlying transport).
- **RFC 9001** &mdash; TLS 1.3 binding for QUIC.
- **RFC 1035 §4.2.2** &mdash; the two-octet length prefix carries
  over to DoQ; one DNS exchange per QUIC stream.
- **RFC 8467** &mdash; padding for encrypted DNS (recommended for
  DoQ as for DoT/DoH).

## Public surface contract

The implementing PR MUST deliver:

```csharp
namespace Assimalign.Cohesion.Dns;

public sealed class DoqDnsTransport : DnsTransport
{
    public DoqDnsTransport(DoqDnsTransportOptions options);

    public override EndPoint Endpoint { get; }

    public override Task<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default);

    protected override ValueTask DisposeAsyncCore();
}

public sealed class DoqDnsTransportOptions
{
    public EndPoint Endpoint { get; set; }              // required, defaults to port 853
    public string Host { get; set; }                    // SNI + cert hostname check
    public TimeSpan ConnectTimeout { get; set; }        // default 5s
    public TimeSpan QueryTimeout { get; set; }          // default 5s

    // QUIC IdleTimeout (RFC 9000 §10.1). Connection closes when
    // idle for this duration. Default 30s, matching the DoT idle
    // close in the Cohesion DoT options.
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Authentication profile, identical semantics to the DoT
    // package (RFC 8310). DoQ inherits DoT's authentication model
    // per RFC 9250 §4.
    public DoqAuthenticationProfile AuthenticationProfile { get; set; }
        // Strict (default) | Opportunistic | Tofu
    public X509Certificate2Collection? PinnedCertificates { get; set; }
    public SslClientAuthenticationOptions? SslOptions { get; set; }

    // RFC 8467 padding (off by default; see RFC 9250 §8).
    public bool PadRequests { get; set; }

    // RFC 9250 §5.5.4 ALPN. MUST be "doq" per IANA registration.
    // Exposed for future-proofing; default is correct and most
    // callers should leave it alone.
    public string Alpn { get; set; } = "doq";
}
```

Plus a factory-builder extension on whatever shape the resolver
builder ends up with:

```csharp
public static IDnsResolverBuilder AddDoqDnsTransport(
    this IDnsResolverBuilder builder,
    Action<DoqDnsTransportOptions> configure);
```

## Behavior

The transport MUST:

1. Open a QUIC connection to `Endpoint` (default port 853 per
   RFC 9250 §4.1.1) with `ConnectTimeout`. ALPN MUST be `doq` per
   the IANA registration referenced in RFC 9250 §5.5.4.
2. Validate the server certificate per `AuthenticationProfile`,
   matching the DoT semantics (Strict / Opportunistic / Tofu).
3. Open one bidirectional QUIC stream per DNS exchange (RFC 9250
   §4.2). The client MUST NOT reuse a stream &mdash; opening more
   streams is cheap with QUIC, and reusing them breaks per-stream
   flow control.
4. Use the RFC 1035 §4.2.2 two-octet length prefix on every
   message. The Message ID MUST be 0 per RFC 9250 §4.2.1 because
   QUIC streams already provide correlation.
5. Reuse the underlying QUIC *connection* for subsequent exchanges
   until `IdleTimeout` elapses, then close gracefully with
   transport error code `DOQ_NO_ERROR` (0).
6. Honour the cancellation token at every async wait.
7. Apply RFC 8467 padding when `PadRequests = true`, padding to a
   468-octet block size per RFC 8467 §4.1 ("recommended block
   sizes for DoT/DoQ").
8. Map every failure to `DnsException`:
   - QUIC handshake failure &rarr; `DnsErrorCode.Transport`
   - Hostname-mismatch &rarr; `DnsErrorCode.Spoofed`
   - Stream reset with a `DOQ_*` error code &rarr;
     `DnsErrorCode.Transport` (carry the DoQ error code in the
     exception data)
   - Timeout &rarr; `DnsErrorCode.Timeout`
   - Non-zero Message ID in response &rarr;
     `DnsErrorCode.Malformed` (RFC 9250 §4.2.1)

## Dependencies

The package MUST depend on:

- `Assimalign.Cohesion.Dns.Client` (for the `DnsTransport` base and
  the resolver-builder integration).
- `Assimalign.Cohesion.Transports` &mdash; specifically the QUIC
  client transport. When that surface isn't available, the
  implementer MUST escalate rather than vendoring a QUIC client
  inside the DoQ package.

The package MUST guard `System.Net.Quic` usage behind
`QuicConnection.IsSupported` and surface a clear
`PlatformNotSupportedException` (or a `DnsException` with
`DnsErrorCode.Transport` and an inner `PlatformNotSupportedException`)
when constructed on a platform without QUIC support.

## Test plan

1. **Round-trip against a real DoQ resolver** (AdGuard
   `94.140.14.140:853`, NextDNS, etc.): opt-in integration test,
   gated on a `DnsLiveTests=true` env var, additionally skipped
   on `!QuicConnection.IsSupported`.
2. **Hostname-mismatch surfaces `DnsErrorCode.Spoofed`**: loopback
   QUIC listener with a self-signed cert whose CN doesn't match
   `Host`.
3. **Strict-profile chain failure surfaces
   `DnsErrorCode.Transport`**: self-signed cert + system trust
   validation.
4. **Message ID MUST be zero on the wire**: stub QUIC server
   inspects the framed request and asserts the first two RDATA
   bytes (after the length prefix) are `0x00 0x00`.
5. **Non-zero Message ID in response surfaces
   `DnsErrorCode.Malformed`**: stub server returns a response with
   ID = 0x1234.
6. **Cancellation mid-handshake**: asserts
   `OperationCanceledException` (not `DnsException`).
7. **AOT analyzer clean** under
   `<IsAotCompatible>true</IsAotCompatible>` &mdash; verify
   `System.Net.Quic` cooperates with AOT on the supported runtimes.
8. **Platform gate**: on macOS-latest (where QUIC isn't supported
   on .NET 10 today), construction MUST raise
   `PlatformNotSupportedException` rather than failing with a
   misleading error.

## Open questions for the implementer

- **Connection pooling**: one QUIC connection per `DoqDnsTransport`
  instance, or a pool keyed by `(Host, Endpoint)`? Match whatever
  shape `Assimalign.Cohesion.Transports.QuicClientTransport` adopts.
- **0-RTT data**: RFC 9250 §4.4 forbids 0-RTT for the first DNS
  query in a session. Defer 0-RTT entirely to a follow-up story.
- **Stream concurrency limit**: QUIC connections have a
  configurable bidirectional-stream limit. The implementing PR
  should pick a sensible default (RFC 9250 §4.2 suggests 100) and
  expose it on the options bag.
- **CI matrix scope**: macOS QUIC support is unreliable on .NET 10
  &mdash; document the platform skip in
  `docs/COMPATIBILITY.md` once the implementation lands.

## Non-goals

- Acting as a DoQ *server*. Server-side resolution lives under the
  future `Assimalign.Cohesion.Dns.Authority` package (Feature
  `.07` of the L01.01.08 epic, currently deferred).
- Implementing QUIC itself. The package consumes
  `System.Net.Quic` (via `Assimalign.Cohesion.Transports`) and
  does not own the cipher selection, congestion control, or
  certificate handling beyond the authentication profile.
- Zero-RTT data exchange. RFC 9250 §4.4 calls 0-RTT out
  explicitly; revisit only if a real workload demands it.
