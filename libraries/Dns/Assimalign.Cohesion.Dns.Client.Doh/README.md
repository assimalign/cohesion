# Assimalign.Cohesion.Dns.Client.Doh

DNS-over-HTTPS (DoH) transport for the Cohesion DNS family — RFC 8484.

**Placeholder package.** No implementation ships in this build. The
package is scaffolded so the family layout is in place when the
implementation lands; the public surface contract lives in
`docs/REQUIREMENTS.md`.

DoH carries DNS messages over HTTP/2 (or HTTP/3) with
`Content-Type: application/dns-message`. The implementation will
build on `Assimalign.Cohesion.Http.ClientFactory` so consumers can
plug their existing `HttpClient` pipeline (auth, retries, proxies)
into the DoH transport without modification.

See `docs/REQUIREMENTS.md` for the full implementation contract.
