# Assimalign.Cohesion.Dns

## Summary

The root contract package for the Cohesion DNS family. Defines the
public surface — `DnsClient`, `DnsResolver`, `DnsAuthority`,
`DnsZone`, `DnsTransport` — plus the wire-format domain model
(`DnsMessage`, `DnsRecord`, `DnsName`, `DnsQuestion`, the
`DnsRecordType` / `DnsClass` / `DnsOpCode` / `DnsResponseCode` enums)
and the `DnsException` + `DnsErrorCode` error model.

The contract layer is modeled as **abstract classes**, not interfaces.
DNS is a protocol-layer library where every concrete implementation
has the same shape, so the abstract-class form lets the base type own
the common plumbing (`IDisposable` + `IAsyncDisposable`,
`ThrowIfDisposed` guards, future telemetry hooks) without each
implementer reinventing it. The trade-off — losing the option to
implement the contract on an existing class — is intentional; DNS
implementations are written from scratch, not retrofitted.

Concrete implementations live in sibling packages:

| Package | Status |
|---------|--------|
| `Assimalign.Cohesion.Dns.Client` | Resolving DNS client (transports + recursive resolver + cache). Scaffolded; implementation lands in Features 05–06. |

Future packages (decided after Feature 06 lands): authoritative server,
DNSSEC validation, per-transport plugins if useful.

## Status

Initial scaffolding. Ships:

- `DnsException` + `DnsErrorCode` (Story `.02.03`, AOT-safe)
- `DnsClient`, `DnsResolver`, `DnsAuthority`, `DnsZone`,
  `DnsTransport` abstract classes (Story `.02.01`)
- `DnsName`, `DnsQuestion`, `DnsRecord` value-type/data-shape stubs
- Wire enums: `DnsRecordType`, `DnsClass`, `DnsOpCode`, `DnsResponseCode`
- `DnsMessage` placeholder — wire-format implementation lands in PR 2

## Public surface

### Errors
- `DnsException` (`Code` and `ResponseCode` virtual)
- `DnsErrorCode` (stable ordinal values; additive-only)
- `[DoesNotReturn]` helpers on `DnsException` so providers don't
  construct exceptions inline.

### Contracts (abstract classes)
- `DnsClient.QueryAsync(question, ct)` — single-question query. Base
  class owns `IDisposable` + `IAsyncDisposable` plumbing with
  `DisposeCore` / `DisposeAsyncCore` override hooks plus a
  `ThrowIfDisposed` guard.
- `DnsResolver : DnsClient` adds `ResolveAsync` (recursive walk) and
  `ClearCacheAsync`.
- `DnsAuthority` exposes `Zones` and `FindZone(name)` for authoritative
  servers, with the same lifecycle plumbing as `DnsClient`.
- `DnsZone` carries `Origin`, `Serial`, `GetRecords`, `Contains`.
- `DnsTransport.ExchangeAsync(endpoint, request, ct)` is the
  network-layer plug point used by the resolver, again with shared
  lifecycle.

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

- 32 tests in `Assimalign.Cohesion.Dns.Tests`:
  - `DnsExceptionTests` — every `Throw*` helper + ordinal-stability theory.
  - `DnsNameTests` — validation, label extraction, case-insensitive
    equality, implicit conversions.
  - `DnsQuestionTests` — equality and ToString.
  - `DnsClientLifecycleTests` — Dispose idempotency,
    DisposeAsync fallback to DisposeCore, DisposeAsyncCore override
    path, ThrowIfDisposed guard. Validates the lifecycle plumbing in
    `DnsClient` that every derived client inherits.

The contract-suite pattern used by FileSystem (provider-agnostic tests
shared via `<Compile Include …>`) is not yet applicable here — that
arrives in PR 2 once `DnsMessage` has a serializer to test against.
