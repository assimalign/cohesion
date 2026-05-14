# Assimalign.Cohesion.Dns.Client.Doq

## Summary

DNS-over-QUIC (DoQ) transport for the Cohesion DNS family.
Implements RFC 9250 ("DNS over Dedicated QUIC Connections") on top
of the wire format defined in `Assimalign.Cohesion.Dns`.

## Status

**Placeholder.** No code in this package today; only the csproj and
docs exist so the family layout is stable. Implementation deferred
until `Assimalign.Cohesion.Transports` ships a QUIC client transport
and the broader Cohesion HTTP / TLS story has matured enough to
share certificate / authentication plumbing across DoT, DoH, and
DoQ.

See `docs/REQUIREMENTS.md` for the implementation contract &mdash;
public surface, expected dependencies, test plan, and open design
questions. PR-3 (the resolver + UDP/TCP transports PR) creates this
placeholder so callers can pin a reference to a stable assembly name
ahead of the eventual implementation.

## Why a separate package

DoQ requires a QUIC stack &mdash; in practice
`System.Net.Quic`, which on .NET 10 is still platform-gated (needs a
working MsQuic on Windows / Linux, unsupported on macOS today). That
runtime cost has no business being paid by users of plain UDP DNS.
Keeping DoQ as its own package:

- Lets users opt out of the QUIC dependency surface entirely
  (including the conditional `System.Net.Quic` reference).
- Mirrors the family layout of the FileSystem packages (one
  provider per package).
- Makes the platform-availability story explicit in the
  documentation rather than a runtime surprise.

## When implementation lands

The implementing PR will:

1. Add a single `DoqDnsTransport : DnsTransport` type plus its
   options bag.
2. Compose on top of
   `Assimalign.Cohesion.Transports.QuicClientTransport` for the
   QUIC connection / stream lifecycle.
3. Register a factory-style extension method (`AddDoqDnsTransport`)
   for the resolver builder.
4. Add the assembly to the CI matrix in
   `.github/workflows/library-dns.yml`. Runs only on
   ubuntu-latest + windows-latest until macOS gets a working QUIC
   stack.
5. Add the assembly to `frameworks/Assimalign.Cohesion.App.props`
   under the active block.
6. Replace this OVERVIEW with the post-implementation version
   (status, public surface, etc.) and either freeze
   `REQUIREMENTS.md` or fold it into the DESIGN doc.
