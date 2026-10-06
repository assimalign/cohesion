# Assimalign.Cohesion.Http.Connections

HTTP/1.1, HTTP/2, and HTTP/3 server transports for the Cohesion
HTTP family. Each transport translates wire-level frames / streams
into the `IHttpRequest` / `IHttpResponse` / `IHttpContext` contracts
defined in `Assimalign.Cohesion.Http`.

## Status

The design record is [`docs/DESIGN.md`](docs/DESIGN.md). Per-version
completeness lives in the L01.01.11 backlog under feature parents
`.06`/`.07`/`.08` (HTTP/2) and `.09`/`.10` (HTTP/3).

## Surface

| Layer | Status |
|-------|--------|
| HTTP/1.1 | Substantial: framing, chunked encoding, content-length, connection reuse, upgrade transitions |
| HTTP/2 | Substantial: HPACK encoder/decoder/tables, frame I/O, stream / connection model |
| HTTP/3 | Substantial: QPACK encoder and decoder (static and dynamic tables), control and request streams over QUIC, GOAWAY |
| TLS | ALPN dispatch serves HTTP/1.1 and HTTP/2 on one endpoint; `IHttpTlsConnectionFeature` exposes the client certificate, protocol, cipher suite and negotiated ALPN protocol |

The transports do **not** parse form bodies; the body stream is
delivered to the application layer via `IHttpRequest.Body` and
parsing is opt-in via the `Assimalign.Cohesion.Http.Forms` package.
The transports do **not** create or attach `IHttpSession`; session
state is application code via `Assimalign.Cohesion.Http.Sessions`.

## Standards

Targets RFC 9112 (HTTP/1.1), RFC 9113 + RFC 7541 HPACK (HTTP/2),
RFC 9114 + RFC 9204 QPACK (HTTP/3), with RFC 9110 for shared
HTTP semantics. One TLS listener can serve HTTP/2 and HTTP/1.1,
chosen per connection through ALPN (RFC 7301).
