# Assimalign.Cohesion.Dns.Client

Resolving DNS client for Cohesion. Provides recursive DNS resolution,
caching, and pluggable UDP / TCP / DoT / DoH / DoQ transports on top
of the `Assimalign.Cohesion.Dns` contracts.

## What ships today

| Surface | Status |
|---------|--------|
| `UdpDnsTransport` &mdash; RFC 1035 §4.2.1 UDP transport | Shipping |
| `TcpDnsTransport` &mdash; RFC 1035 §4.2.2 length-prefix TCP transport, RFC 7766 connection reuse | Shipping |
| `DotDnsTransport`, `DohDnsTransport`, `DoqDnsTransport` | Placeholder packages (see `Assimalign.Cohesion.Dns.Client.{Dot,Doh,Doq}`) |
| Recursive resolver + cache | Deferred to a follow-up PR |

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
