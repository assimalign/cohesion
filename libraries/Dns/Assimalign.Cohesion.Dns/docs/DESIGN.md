# Assimalign.Cohesion.Dns — Design

## Design intent

A single set of public contracts every Cohesion DNS package implements
identically. Callers depend on the contracts, not the transport or the
deployment shape, so a service can swap a stub client for a recursive
resolver, or layer an authoritative server underneath, without changing
the consumer surface.

The package is intentionally narrow:

- One async query method per shape (`IDnsClient.QueryAsync`,
  `IDnsResolver.ResolveAsync`).
- One transport plug point (`IDnsTransport.ExchangeAsync`).
- One wire-format domain model (`DnsName`, `DnsQuestion`, `DnsRecord`,
  `DnsMessage`) shared by every layer.
- One exception type (`DnsException`) with an explicit, additive error
  enum (`DnsErrorCode`).

## Family map

| Package | Role | Dependencies |
|---------|------|---------------|
| `Assimalign.Cohesion.Dns` | Contracts + wire format | `Assimalign.Cohesion.Core` |
| `Assimalign.Cohesion.Dns.Client` | Resolving client + transports | `Assimalign.Cohesion.Dns` |
| Future: `…Dns.Authority` | Authoritative server | `Assimalign.Cohesion.Dns` |
| Future: `…Dns.Dnssec` | DNSSEC validation primitives | `Assimalign.Cohesion.Dns` |

Dependency direction is one-way: every package depends on the root
package; no package depends on a sibling. Transport plugins (if they
become separate packages later) would depend on `Dns` for `IDnsTransport`
and not on each other.

## Error model

`DnsException` carries a `DnsErrorCode` plus an optional wire
`DnsResponseCode` when the failure originated upstream. Static
`[DoesNotReturn]` helpers (`ThrowNotFound`, `ThrowServerFailure`,
`ThrowTimeout`, `ThrowMalformed`, `ThrowSpoofed`,
`ThrowDnssecValidationFailed`, `ThrowTransport`, `ThrowReadOnly`,
`ThrowTsigVerificationFailed`) let providers raise the right shape
without inline construction.

The mapping from wire RCODE to `DnsErrorCode`:

| Wire RCODE | `DnsErrorCode` | Notes |
|------------|----------------|-------|
| `NoError` | — (success) | Successful answer. |
| `NXDomain` | `NotFound` | Name does not exist. |
| `FormErr` | `ServerFailure` | Server couldn't parse the query. |
| `ServFail` | `ServerFailure` | Upstream malfunction. |
| `NotImp` | `ServerFailure` | Server doesn't support the query type. |
| `Refused` | `ServerFailure` | Policy refusal. |
| `BadVers`/`BadKey`/`BadTime`/&#8230; | `ServerFailure` (TSIG variants → `TsigVerificationFailed`) | Extended RCODEs. |

Network-level failures map to `DnsErrorCode.Transport` or `Timeout`
without an RCODE. Validation failures (DNSSEC, TSIG, spoofing) carry
the dedicated codes for the specific failure mode.

## Wire-format scope

`DnsMessage` is currently a placeholder. PR 2 (Feature 03) will:

1. Add the binary read/write surface — `DnsMessage.Parse(ReadOnlySpan<byte>)`
   and `DnsMessage.TryWriteTo(Span<byte>, out int written)`.
2. Implement RFC 1035 §4 name compression on both sides.
3. Add strongly-typed `DnsRecord` subclasses for A / AAAA / CNAME / MX /
   TXT / NS / SOA / PTR / SRV / OPT.
4. Build a golden-corpus test against well-known packets so any future
   change has to round-trip the same bytes.

PR 3 (Feature 04) adds EDNS OPT handling on top.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is set globally for the
`libraries/` tree, so the AOT analyzer runs at build time. The public
surface intentionally avoids:

- Open generics that would require reflection emit
- Runtime-discovered record types (the typed `DnsRecord` family will
  be a sealed hierarchy, not plugin-loaded)
- `Type.GetType(string)`-style lookups

If a future story needs dynamic dispatch (e.g., user-extensible RR
types), it ships as an explicit opt-in extension point, not as
implicit reflection.

## Naming + path model

DNS names are case-insensitive (RFC 1035 §2.3.3) but the public
`DnsName` value type preserves the input's casing for display.
Equality and hashing normalize:

```csharp
DnsName a = "Example.COM";
DnsName b = "example.com.";
Assert.Equal(a, b);                       // true
Assert.Equal(a.GetHashCode(), b.GetHashCode());
Assert.Equal("Example.COM", a.Value);    // preserved
Assert.Equal("example.com.", b.Value);   // preserved
```

The trailing dot marks a fully-qualified name in presentation form; the
value type treats names with and without the trailing dot as equal so
callers don't have to be defensive.

## Adding a new package to the family

1. Create `libraries/Dns/Assimalign.Cohesion.Dns.<Name>/src/` and
   `tests/`.
2. Reference `Assimalign.Cohesion.Dns` via
   `<CohesionProjectReference Include="Assimalign.Cohesion.Dns" />`.
3. Implement the relevant interface(s).
4. Add an entry to the workflow matrix in
   `.github/workflows/library-dns.yml`.
5. Register the assembly in `frameworks/Assimalign.Cohesion.App.props`
   under the active `<CohesionFrameworkAssembly>` block.
6. Add `docs/OVERVIEW.md` + `docs/DESIGN.md` + populate the package
   `README.md`.

## Non-goals

- **A drop-in BIND/Unbound replacement.** The package family targets
  Cohesion's application model, not zone-hosting at internet scale.
- **Runtime-extensible RR types via reflection.** A future opt-in
  extension point may emerge; the default surface stays sealed and
  AOT-clean.
- **Compatibility with the old (deleted) Technitium-derived surface.**
  Names and shapes are being redefined freely.
