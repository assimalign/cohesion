# Assimalign.Cohesion.Dns.Client.Dot

## Summary

DNS-over-TLS (DoT) transport for the Cohesion DNS family. Implements
RFC 7858 ("Specification for DNS over Transport Layer Security
(TLS)") on top of the wire format defined in
`Assimalign.Cohesion.Dns`.

## Status

**Placeholder.** No code in this package today; only the csproj and
docs exist so the family layout is stable. Implementation deferred
until the broader Cohesion HTTP / TLS story matures.

See `docs/REQUIREMENTS.md` for the implementation contract — public
surface, expected dependencies, test plan, and open design
questions. PR-3 (the resolver + UDP/TCP transports PR) creates this
placeholder so callers can pin a reference to a stable assembly name
ahead of the eventual implementation.

## Why a separate package

DoT shares the wire format of regular DNS-over-TCP (RFC 1035 §4.2.2
two-octet length-prefix framing) but adds a TLS layer between the
TCP socket and the framing. Keeping it as a separate package:

- Lets users opt out of the TLS dependency surface when they only
  need UDP/TCP.
- Mirrors the family layout of the FileSystem packages
  (one provider per package).
- Makes the runtime / AOT cost of TLS explicit.

## When implementation lands

The implementing PR will:

1. Add a single `DotDnsTransport : DnsTransport` type plus its
   options bag.
2. Register a factory-style extension method
   (`AddDotDnsTransport`) for the resolver builder.
3. Add the assembly to the CI matrix in
   `.github/workflows/library-dns.yml`.
4. Add the assembly to `frameworks/Assimalign.Cohesion.App.props`
   under the active block.
5. Replace this OVERVIEW with the post-implementation version (status,
   public surface, etc.) and either freeze `REQUIREMENTS.md` or fold
   it into the DESIGN doc.
