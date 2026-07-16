# Assimalign.Cohesion.Http.DigestFields

RFC 9530 integrity digest fields for the Cohesion HTTP stack — `Content-Digest`, `Repr-Digest`,
`Want-Content-Digest`, `Want-Repr-Digest` — as structured-fields value objects with `sha-256` /
`sha-512` computation and verification (BCL incremental hashing, no reflection), plus an opt-in
server-side request verifier through the core `IHttpExchangeInterceptor` seam: a `Content-Digest`
mismatch is a pre-dispatch `400` on HTTP/1.1 and HTTP/3, and on HTTP/2 — whose body streams in
under flow control — a lazy verify-on-read failure surfaced on the terminal body read.

- `docs/OVERVIEW.md` — purpose, dependencies, usage
- `docs/DESIGN.md` — value-model placement, algorithm registry, verification model, and scope
