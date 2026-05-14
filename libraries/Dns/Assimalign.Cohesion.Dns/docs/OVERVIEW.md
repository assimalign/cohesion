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

Contracts (PR 1) plus the complete wire format and EDNS layer (PR 2).
Now ships:

- `DnsException` + `DnsErrorCode` (AOT-safe)
- `DnsClient`, `DnsResolver`, `DnsAuthority`, `DnsZone`,
  `DnsTransport` abstract classes
- `DnsName`, `DnsQuestion`, `DnsHeader`, `DnsHeaderFlags` value types
- `DnsMessage` with `Parse(ReadOnlySpan<byte>)` and
  `WriteTo(Span<byte>)` — RFC 1035 §4 wire format including §4.1.4
  name compression on both sides (pointer-chain depth + offset-
  direction validation)
- `DnsRecord` abstract base + the strongly-typed family:
  `DnsARecord` / `DnsAaaaRecord` / `DnsCnameRecord` / `DnsNsRecord` /
  `DnsPtrRecord` / `DnsMxRecord` / `DnsTxtRecord` / `DnsSoaRecord` /
  `DnsSrvRecord` / `DnsOptRecord` / `DnsUnknownRecord` (RFC 3597
  round-trip for unrecognised types)
- EDNS OPT pseudo-record (RFC 6891) with the typed option family:
  `DnsEdnsClientSubnetOption` (RFC 7871),
  `DnsEdnsCookieOption` (RFC 7873),
  `DnsEdnsExtendedErrorOption` (RFC 8914),
  `DnsEdnsUnknownOption` for forward compatibility
- Wire enums: `DnsRecordType`, `DnsClass`, `DnsOpCode`,
  `DnsResponseCode`, `DnsEdnsFlags`, `DnsEdnsOptionCode`

The concrete `DnsClient.Client` package &#8211; transports + recursive
resolver &#8211; lands in PR 3.

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
- `DnsTransport.ExchangeAsync(request, ct)` is the network-layer plug
  point used by the resolver. One transport binds to one
  `Endpoint` (set at construction), matching the
  `Assimalign.Cohesion.Transports` family's `ClientTransport` shape so
  PR 3 transports can adapt cleanly. Resolvers that need to fail over
  hold a list of transports rather than a single transport with N
  endpoints.

### Wire-level types
- `DnsName` — RFC 1035 §2.3.3 case-insensitive name, validates
  label/total-length limits at construction.
- `DnsQuestion(name, type, class = IN)` — readonly value type.
- `DnsHeader` — 12-octet header (RFC 1035 §4.1.1) with field-level
  parse/write. Carries `Id`, `Flags`, `OpCode`, `ResponseCode`, and
  the four section counts.
- `DnsHeaderFlags` — `[Flags]` enum for QR / AA / TC / RD / RA / AD / CD.
- `DnsMessage` — full DNS message with `Parse` + `WriteTo`. Section
  counts are derived from the actual collection sizes on write, so
  callers don't have to keep the header in sync.
- `DnsRecord` — abstract base for every typed record. Subclasses
  expose strongly-typed fields (e.g. `DnsARecord.Address`,
  `DnsSoaRecord.Serial`). Unknown wire types parse as
  `DnsUnknownRecord` preserving the opaque RDATA bytes per RFC 3597.

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

- 58 tests in `Assimalign.Cohesion.Dns.Tests`:
  - `DnsExceptionTests` — every `Throw*` helper + ordinal-stability theory.
  - `DnsNameTests` — validation, label extraction, case-insensitive
    equality, implicit conversions.
  - `DnsQuestionTests` — equality and ToString.
  - `DnsClientLifecycleTests` — Dispose idempotency,
    DisposeAsync fallback to DisposeCore, DisposeAsyncCore override
    path, ThrowIfDisposed guard.
  - `DnsHeaderTests` — header round-trip, OPCODE/RCODE bit packing,
    DNSSEC AD/CD bits, under-sized buffer rejection.
  - `DnsMessageTests` — query and answer round-trips for every
    supported record type (A / AAAA / CNAME / NS / PTR / MX / TXT /
    SOA / SRV), name-compression smoke test (size-bound on
    serialized output), unknown-RR round-trip (RFC 3597), malformed-
    name rejection.
  - `DnsEdnsTests` — OPT round-trip with payload size + DO bit, ECS
    option (IPv4 + IPv6), Cookie option (client-only + client+server),
    Extended-DNS-Error option, unknown-option preservation, multi-
    option ordering, extended-RCODE high byte, malformed ECS family.
