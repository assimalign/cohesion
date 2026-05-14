# Assimalign.Cohesion.Dns

The Cohesion DNS family. Provides a uniform `IDnsClient` / `IDnsResolver` /
`IDnsAuthority` surface across multiple DNS deployment shapes — stub
client, recursive resolver, authoritative server — all on top of a single
wire-format domain model.

## Packages

| Package | Role | Status |
|---------|------|--------|
| `Assimalign.Cohesion.Dns` | Root contracts: `IDnsClient`, `IDnsResolver`, `IDnsAuthority`, `IDnsZone`, `IDnsTransport`, plus the wire-format types (`DnsMessage`, `DnsRecord`, `DnsName`, `DnsQuestion`, RR/class/op/RCODE enums) and `DnsException`. | Scaffolded — wire formats land in Feature 03. |
| `Assimalign.Cohesion.Dns.Client` | Resolving DNS client: recursive resolver, cache, pluggable UDP / TCP / DoT / DoH / DoQ transports. | Scaffolded — implementation lands in Features 05–06. |

Future packages (decided after Feature 06 lands): `.Authority` for
authoritative serving, `.Dnssec` for validation, and per-transport plugins
if granularity becomes useful.

## Provenance & legal posture

The Cohesion DNS area was previously seeded with code carrying a
**GPL v3+** copyright header from the Technitium Library. Cohesion ships
under the MIT license, which is incompatible with GPL copyleft. As of the
epic-opening PR, every file with that header has been removed and the area
is being re-implemented as a **clean-room build from the published RFCs**.
The full audit trail lives in
[`Assimalign.Cohesion.Dns/docs/PROVENANCE.md`](Assimalign.Cohesion.Dns/docs/PROVENANCE.md).

**Rule for contributors:** Do not paste, transcribe, or reference the
removed Technitium code (or any other DNS library you've seen with a
restrictive license). Read the RFC, write the implementation from the
specification. RFCs define facts (wire layouts, field widths, type
numbers) which are not copyrightable; specific code expressions of them
are.

## Conventions

- Single `IDnsClient` contract — every concrete provider returns a
  `DnsMessage` and signals failure through `DnsException` with an
  explicit `DnsErrorCode`.
- Async-only public surface; DNS is a network operation.
- All names use the `DnsName` value type (presentation form, '/'
  separators are illegal — DNS uses '.').
- Wire-level enums (`DnsRecordType`, `DnsClass`, `DnsOpCode`,
  `DnsResponseCode`) hold IANA-assigned values; numeric values are
  stable and additive.
- AOT-clean: `<IsAotCompatible>true</IsAotCompatible>` is set globally
  for `libraries/` and verified by the analyzer at build time.

See each package's `docs/OVERVIEW.md` and `docs/DESIGN.md` for details.
