# Assimalign.Cohesion.Dns.Client.Doh

## Summary

DNS-over-HTTPS (DoH) transport for the Cohesion DNS family.
Implements RFC 8484 ("DNS Queries over HTTPS (DoH)") on top of the
wire format defined in `Assimalign.Cohesion.Dns`.

## Status

**Placeholder.** No code in this package today; only the csproj and
docs exist so the family layout is stable. Implementation deferred
until `Assimalign.Cohesion.Http.ClientFactory` matures &mdash; the DoH
transport is designed as a thin adapter on top of the Cohesion HTTP
client factory, and standing it up before that surface is finalized
would only bake in churn.

See `docs/REQUIREMENTS.md` for the implementation contract &mdash;
public surface, expected dependencies, test plan, and open design
questions. PR-3 (the resolver + UDP/TCP transports PR) creates this
placeholder so callers can pin a reference to a stable assembly name
ahead of the eventual implementation.

## Why a separate package

DoH layers DNS messages over HTTP/2 (or HTTP/3) with
`Content-Type: application/dns-message`. That means the transport
pulls in the entire HTTP client stack &mdash; auth handlers, retry
policies, proxy resolution, HTTP/2 multiplexing &mdash; that pure
UDP/TCP DNS users have no use for. Keeping DoH in its own package:

- Lets users opt out of the HTTP dependency surface when they only
  need UDP/TCP/DoT.
- Mirrors the family layout of the FileSystem packages (one
  provider per package).
- Lets the DoH transport integrate cleanly with whatever
  authentication, retries, and observability the host has already
  configured on its `HttpClient` pipeline &mdash; just hand the
  transport an `IHttpClientFactory` and a logical-name string.

## When implementation lands

The implementing PR will:

1. Add a single `DohDnsTransport : DnsTransport` type plus its
   options bag.
2. Wire it to `Assimalign.Cohesion.Http.ClientFactory` for
   `HttpClient` resolution.
3. Register a factory-style extension method (`AddDohDnsTransport`)
   for the resolver builder.
4. Add the assembly to the CI matrix in
   `.github/workflows/library-dns.yml`.
5. Add the assembly to `frameworks/Assimalign.Cohesion.App.props`
   under the active block.
6. Replace this OVERVIEW with the post-implementation version
   (status, public surface, etc.) and either freeze
   `REQUIREMENTS.md` or fold it into the DESIGN doc.
