# Assimalign.Cohesion.Dns

Root contract package for the Cohesion DNS family. Defines
`IDnsClient`, `IDnsResolver`, `IDnsAuthority`, `IDnsZone`,
`IDnsTransport`, the wire-format domain model (`DnsMessage`,
`DnsRecord`, `DnsName`, `DnsQuestion`, the `DnsRecordType` /
`DnsClass` / `DnsOpCode` / `DnsResponseCode` enums), and the
`DnsException` + `DnsErrorCode` error model.

```csharp
using Assimalign.Cohesion.Dns;

IDnsResolver resolver = /* from Assimalign.Cohesion.Dns.Client */;
DnsMessage response = await resolver.ResolveAsync(
    new DnsQuestion("example.com", DnsRecordType.A));
```

Pair this package with a concrete provider:

| Provider | Role |
|----------|------|
| `Assimalign.Cohesion.Dns.Client` | Resolving DNS client with pluggable UDP / TCP / DoT / DoH / DoQ transports. |

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for details, and
`docs/PROVENANCE.md` for the audit + clean-room rules.
