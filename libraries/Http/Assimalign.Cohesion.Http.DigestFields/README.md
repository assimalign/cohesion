# Assimalign.Cohesion.Http.DigestFields

RFC 9530 integrity digest fields for the Cohesion HTTP stack — `Content-Digest`, `Repr-Digest`,
`Want-Content-Digest`, `Want-Repr-Digest` — as structured-fields value objects with `sha-256` /
`sha-512` computation and verification (BCL incremental hashing, no reflection), plus an opt-in
server-side request verifier that rejects a `Content-Digest` mismatch with `400` at parse time
through the core `IHttpExchangeInterceptor` seam.

- `docs/OVERVIEW.md` — purpose, dependencies, usage
- `docs/DESIGN.md` — value-model placement, algorithm registry, verification model, and scope
