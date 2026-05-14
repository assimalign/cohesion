# Assimalign.Cohesion.Dns

## Summary

The root contract package for the Cohesion DNS family. Defines the
public surface — `IDnsClient`, `IDnsResolver`, `IDnsAuthority`,
`IDnsZone`, `IDnsTransport` — plus the wire-format domain model
(`DnsMessage`, `DnsRecord`, `DnsName`, `DnsQuestion`, the
`DnsRecordType` / `DnsClass` / `DnsOpCode` / `DnsResponseCode` enums)
and the `DnsException` + `DnsErrorCode` error model.

Concrete implementations live in sibling packages:

| Package | Status |
|---------|--------|
| `Assimalign.Cohesion.Dns.Client` | Resolving DNS client (transports + recursive resolver + cache). Scaffolded; implementation lands in Features 05–06. |

Future packages (decided after Feature 06 lands): authoritative server,
DNSSEC validation, per-transport plugins if useful.

## Status

Initial scaffolding. Ships:

- `DnsException` + `DnsErrorCode` (Story `.02.03`, AOT-safe)
- `IDnsClient`, `IDnsResolver`, `IDnsAuthority`, `IDnsZone`,
  `IDnsTransport` interfaces (Story `.02.01`)
- `DnsName`, `DnsQuestion`, `DnsRecord` value-type/data-shape stubs
- Wire enums: `DnsRecordType`, `DnsClass`, `DnsOpCode`, `DnsResponseCode`
- `DnsMessage` placeholder — wire-format implementation lands in PR 2

## Public surface

### Errors
- `DnsException` (sealed-ish — `Code` and `ResponseCode` are virtual)
- `DnsErrorCode` (stable ordinal values; additive-only)
- `[DoesNotReturn]` helpers on `DnsException` so providers don't
  construct exceptions inline.

### Contracts
- `IDnsClient.QueryAsync(question, ct)` — single-question query.
- `IDnsResolver : IDnsClient` adds `ResolveAsync` (recursive walk) and
  `ClearCacheAsync`.
- `IDnsAuthority` exposes `Zones` and `FindZone(name)` for
  authoritative servers.
- `IDnsZone` carries `Origin`, `Serial`, `GetRecords`, `Contains`.
- `IDnsTransport.ExchangeAsync(endpoint, request, ct)` is the
  network-layer plug point used by the resolver.

### Wire-level types
- `DnsName` — RFC 1035 §2.3.3 case-insensitive name, validates
  label/total-length limits at construction.
- `DnsQuestion(name, type, class = IN)` — readonly value type.
- `DnsRecord(name, type, class, ttl, data)` — opaque RDATA today; the
  typed record family lands in PR 2.
- `DnsMessage(id, question)` — placeholder; wire format in PR 2.

### Wire-level enums
- `DnsRecordType` — A / AAAA / CNAME / MX / TXT / NS / SOA / PTR /
  HINFO / SRV / OPT / DS / RRSIG / NSEC / DNSKEY / NSEC3 / NSEC3PARAM /
  TLSA / SVCB / HTTPS / CAA / ANY.
- `DnsClass` — IN / CS / CH / HS / Any.
- `DnsOpCode` — Query / InverseQuery / Status / Notify / Update / DSO.
- `DnsResponseCode` — NoError / FormErr / ServFail / NXDomain /
  NotImp / Refused / YX*-RRSet / NotAuth / NotZone / DsoTypeNI plus
  extended RCODEs (BadVers, BadKey, BadTime, BadCookie, &#8230;).

## What this package does NOT do

By design, this package owns contracts only. It does NOT:

- Open sockets, resolve DNS, or talk to upstreams (that's `Dns.Client`)
- Maintain caches or trust anchors (resolver-specific)
- Sign or validate DNSSEC chains (future `Dns.Dnssec`)
- Serve authoritative responses (future `Dns.Authority`)

## Test coverage

- 22 tests in `Assimalign.Cohesion.Dns.Tests`:
  - `DnsExceptionTests` — every `Throw*` helper + ordinal-stability theory.
  - `DnsNameTests` — validation, label extraction, case-insensitive
    equality, implicit conversions.
  - `DnsQuestionTests` — equality and ToString.

The contract-suite pattern used by FileSystem (provider-agnostic tests
shared via `<Compile Include …>`) is not yet applicable here — that
arrives in PR 2 once `DnsMessage` has a serializer to test against.
