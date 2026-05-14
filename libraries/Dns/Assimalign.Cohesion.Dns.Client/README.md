# Assimalign.Cohesion.Dns.Client

Resolving DNS client for Cohesion. Provides recursive DNS resolution,
caching, and pluggable UDP / TCP / DoT / DoH / DoQ transports on top
of the `Assimalign.Cohesion.Dns` contracts.

## What ships today

| Surface | Status |
|---------|--------|
| `UdpDnsTransport` &mdash; RFC 1035 §4.2.1 UDP transport | Shipping |
| `TcpDnsTransport` &mdash; RFC 1035 §4.2.2 length-prefix TCP transport, RFC 7766 connection reuse | Shipping |
| `StubDnsClient` &mdash; one-shot non-recursive client over a single transport | Shipping |
| `ForwardingDnsResolver` &mdash; cache-aware forwarding resolver, RFC 5452 spoof protection, RFC 5966 TC fallback, RFC 2308 negative caching | Shipping |
| `IterativeDnsResolver` &mdash; iterative resolver from root hints with bailiwick + glue policy + QNAME minimization (RFC 9156) | Shipping |
| `DotDnsTransport`, `DohDnsTransport`, `DoqDnsTransport` | Placeholder packages (see `Assimalign.Cohesion.Dns.Client.{Dot,Doh,Doq}`) |
| Out-of-bailiwick NS-name resolution + delegation caching + EDNS Cookies (RFC 7873) + 0x20 case randomization | Deferred to a follow-up PR |

## Transport contract

Both `UdpDnsTransport` and `TcpDnsTransport` derive from
`Assimalign.Cohesion.Dns.DnsTransport`. Each instance binds to one
remote endpoint and exposes a single `ExchangeAsync` method:

```csharp
var transport = new UdpDnsTransport(new UdpDnsTransportOptions
{
    EndPoint     = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53),
    QueryTimeout = TimeSpan.FromSeconds(2),
});

byte[] request = /* serialized DnsMessage */;
ReadOnlyMemory<byte> response = await transport.ExchangeAsync(request);
DnsMessage parsed = DnsMessage.Parse(response.Span);
```

The transport works in terms of raw byte buffers; serialization and
parsing live in `Assimalign.Cohesion.Dns`. This split keeps the
transport implementation small and lets the resolver decide
message-level concerns like EDNS payload size or DNSSEC bits without
involving the network layer.

## Iterative resolver

`IterativeDnsResolver` walks the delegation chain from configured root hints,
following NS referrals zone by zone until an authoritative server answers
the question. No forwarder is involved; the resolver does the recursion
itself.

```csharp
var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
{
    // Defaults to DnsRootHints.Iana() (26 IPv4+IPv6 endpoints). Override for
    // private DNS / split-horizon setups.
    // RootEndpoints       = new List<IPEndPoint> { ... },
    QueryTimeout            = TimeSpan.FromSeconds(15),
    EnableQNameMinimization = true,   // RFC 9156
});

DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
```

The resolver enforces:

- **RFC 5452** transaction-id + question-echo validation on every response.
- **Bailiwick policy** — NS records that delegate to a zone outside the
  queried authority's bailiwick are rejected as spoofed referrals.
- **In-bailiwick glue policy** — A/AAAA records in the additional section
  are only trusted when their owner name is inside the delegated zone.
  Out-of-bailiwick glue is discarded so a malicious authority cannot
  poison an unrelated zone.
- **QNAME minimization (RFC 9156)** when `EnableQNameMinimization = true`:
  each step probes with only the labels the current authority needs to
  know, hiding the rest of the QNAME until we reach an authority closer
  to the leaf.
- **RFC 5966 TC→TCP fallback** per step when `TcpTransportFactory` is set.
- **Budget enforcement** — bounded referral depth (default 30) and
  bounded total upstream exchanges (default 50) per resolve.

> **PR-5 limitation:** if a referral arrives with no in-bailiwick glue,
> the resolver currently surfaces `DnsErrorCode.Transport` rather than
> recursing to resolve the out-of-bailiwick NS name. Well-glued
> production zones (the IANA root + every TLD that matters) are
> unaffected. Out-of-bailiwick NS resolution lands in a follow-up.

## Stub client

`StubDnsClient` is the simplest concrete `DnsClient`: one transport, one
exchange per call, no cache, no recursion. Use it for testing fixtures,
low-level debugging, or talking directly to a known authoritative server.

```csharp
using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
{
    EndPoint = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53),
});

var stub = new StubDnsClient(new StubDnsClientOptions
{
    Transport = udp,
    RecursionDesired = true,   // false when talking to an authoritative server directly
});

DnsMessage answer = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
```

## Forwarding resolver

`ForwardingDnsResolver` is a cache-aware client that delegates the recursive
walk to one or more upstream resolvers (a corporate DNS server, a public
service like `1.1.1.1` / `9.9.9.9`, a local `unbound`). It's the right shape
for stub resolvers, side-car caches, and any deployment where a trusted
upstream already exists.

```csharp
using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
{
    EndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53),
});
using var tcp = new TcpDnsTransport(new TcpDnsTransportOptions
{
    EndPoint = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53),
});

var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
{
    Forwarders     = { udp },
    TcpFallbacks   = { [udp] = tcp },   // retry on TCP when UDP comes back truncated
    QueryTimeout   = TimeSpan.FromSeconds(5),
    EdnsPayloadSize = 1232,             // RFC 6891 + dnsflagday.net guidance
    MaxCacheTtl    = TimeSpan.FromHours(1),
});

DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
```

The resolver:

- **Caches** positive answers, NXDOMAIN, and NODATA (RFC 2308) by question.
  Negative caching uses `min(SOA.TTL, SOA.MINIMUM)` from the authority
  section. Cached non-success responses re-throw the same exception shape
  on the next call so callers don't have to distinguish hit from miss.
- **Validates** every response per RFC 5452: the transaction id, QR flag,
  and question triple (name + type + class) must echo the outgoing query.
  Failures surface as `DnsException(Spoofed)`. Transaction ids are drawn
  from `RandomNumberGenerator` for every query.
- **Fails over** to the next configured forwarder when an exchange fails
  with `Transport` or `Timeout`. Authoritative non-success responses
  (NXDOMAIN, SERVFAIL, REFUSED) are not retried &mdash; they're the answer.
- **Falls back to TCP** when a UDP response carries the TC flag and a TCP
  transport is registered in `TcpFallbacks` (RFC 5966). When no TCP
  fallback exists the truncated response is returned as-is.

Iterative resolution from root hints &mdash; referral chasing, bailiwick
checks, glue policing, QNAME minimization &mdash; is a separate concern
that lands in a follow-up.

## Connection reuse + retry semantics

`TcpDnsTransport` follows RFC 7766: one TCP connection per transport
instance, reused across exchanges. Connection lifecycle:

- **Idle close**: connections older than `IdleTimeout` (default 30s)
  are recycled before the next exchange.
- **Stale recovery**: if a reused connection fails mid-exchange (RST,
  EOF), the transport opens a fresh connection and retries the
  exchange once. This is safe because DNS queries are idempotent.
  Fresh connections that fail are not retried &mdash; the failure
  surfaces to the caller as `DnsException(Transport)`.
- **External cancellation** propagates as
  `OperationCanceledException`; internal timeout surfaces as
  `DnsException(Timeout)`.

## Implementation note: raw sockets vs Cohesion.Transports

Both transports use `System.Net.Sockets.Socket` directly rather than
building on `Assimalign.Cohesion.Transports.{Udp,Tcp}ClientTransport`.
The Transports library models bidirectional, pipe-backed flows with
pluggable middleware &mdash; the right model for streaming protocols
and servers, gratuitous machinery for one-shot DNS request/response.

The design notes on each transport class spell this out. If the
Transports library later grows a request/response shape we can
revisit; the public surface here doesn't change.

## See also

- [`Assimalign.Cohesion.Dns`](../Assimalign.Cohesion.Dns/docs/OVERVIEW.md) &mdash;
  wire format contracts and the `DnsTransport` abstract base.
- [`Assimalign.Cohesion.Dns/docs/PROVENANCE.md`](../Assimalign.Cohesion.Dns/docs/PROVENANCE.md) &mdash;
  the clean-room rules that govern every package in this epic.
- `Assimalign.Cohesion.Dns.Client.{Dot,Doh,Doq}` &mdash; placeholder
  packages with full implementation contracts in their
  `docs/REQUIREMENTS.md`.
