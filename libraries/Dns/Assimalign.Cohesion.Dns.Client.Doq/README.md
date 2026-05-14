# Assimalign.Cohesion.Dns.Client.Doq

DNS-over-QUIC (DoQ) transport for the Cohesion DNS family — RFC 9250.

**Placeholder package.** No implementation ships in this build. The
package is scaffolded so the family layout is in place when the
implementation lands; the public surface contract lives in
`docs/REQUIREMENTS.md`.

DoQ carries DNS messages over QUIC streams using the same two-octet
length-prefix framing as DNS-over-TCP (RFC 1035 §4.2.2), with one
DNS exchange per QUIC stream. The implementation will build on
`Assimalign.Cohesion.Transports.QuicClientTransport` once that
surface is feature-complete, so connection lifecycle, 0-RTT, and
congestion control reuse the shared transport story rather than
being reimplemented here.

See `docs/REQUIREMENTS.md` for the full implementation contract.
