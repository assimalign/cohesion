# Assimalign.Cohesion.Http.Transports

HTTP/1.1, HTTP/2, and HTTP/3 server transports for the Cohesion
HTTP family. Each transport translates wire-level frames / streams
into the `IHttpRequest` / `IHttpResponse` / `IHttpContext` contracts
defined in `Assimalign.Cohesion.Http`.

## Status

This package is mid-implementation across the HTTP/2 and HTTP/3
work. See `DESIGN_SUGGESTION.md` for the in-flight architecture
notes. Per-version completeness lives in the L01.01.11 backlog
under feature parents `.06`/`.07`/`.08` (HTTP/2) and `.09`/`.10`
(HTTP/3).

## Surface

| Layer | Status |
|-------|--------|
| HTTP/1.1 | Substantial: framing, chunked encoding, content-length, connection reuse, upgrade transitions |
| HTTP/2 | Substantial: HPACK encoder/decoder/tables, frame I/O, stream / connection model |
| HTTP/3 | Stub: QPACK encoder only, basic QUIC variable-length integer support, no decoder |

The transports do **not** parse form bodies; the body stream is
delivered to the application layer via `IHttpRequest.Body` and
parsing is opt-in via the `Assimalign.Cohesion.Http.Forms` package.
The transports do **not** create or attach `IHttpSession`; session
state is application code via `Assimalign.Cohesion.Http.Sessions`.

## Standards

Targets RFC 9112 (HTTP/1.1), RFC 9113 + RFC 7541 HPACK (HTTP/2),
RFC 9114 + RFC 9204 QPACK (HTTP/3), with RFC 9110 for shared
HTTP semantics.
