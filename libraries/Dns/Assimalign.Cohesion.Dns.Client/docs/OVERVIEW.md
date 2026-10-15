# Assimalign.Cohesion.Dns.Client

This package implements StubDnsClient, ForwardingDnsResolver and IterativeDnsResolver, plus UDP
and TCP transports, using the contracts and wire format from Assimalign.Cohesion.Dns. See the
[package README](../README.md) for query and resolver examples and the [design](DESIGN.md) for
configuration ownership, caching and transport lifecycle.

## Configure before construction

Set options, endpoint addresses, forwarders and fallback mappings before constructing a client,
resolver or transport. Each instance captures those values. To apply later configuration changes,
construct a new instance and dispose the old instance when its outstanding work is complete.

```csharp
var options = new UdpDnsTransportOptions
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 53),
    MaxResponseSize = 4096,
    QueryTimeout = TimeSpan.FromSeconds(2),
};
using var transport = new UdpDnsTransport(options);
using var client = new StubDnsClient(new StubDnsClientOptions { Transport = transport });
DnsMessage answer = await client.QueryAsync(
    new DnsQuestion("example.com", DnsRecordType.A), cancellationToken);
```

Changing options after construction does not affect these instances. In particular, setting an
iterative budget to zero, NS sub-resolution depth to a negative value, or UDP response size to a
negative value cannot invalidate an already constructed resolver or transport. Invalid initial
numeric limits and timeouts raise ArgumentOutOfRangeException. Zero NS sub-resolution depth
remains valid and disables those sub-resolutions; UDP buffers may be as small as one octet.

The stub and forwarding resolver retain references to caller-owned transports; keep those
transports alive for the resolver's use and dispose them separately. Iterative factories return
fresh transports that the resolver disposes per exchange. Endpoints other than IPEndPoint, plus factory and time-source
implementations, keep their own behavior and lifetime; they are not deep-cloned.

## Tests

Build the repository tasks first with `dotnet build build/Tasks -c Debug`, then run
`dotnet test libraries/Dns/Assimalign.Cohesion.Dns.Client/tests/Assimalign.Cohesion.Dns.Client.Tests.csproj`.
The regression suite uses local fixtures and does not require an external DNS service.
