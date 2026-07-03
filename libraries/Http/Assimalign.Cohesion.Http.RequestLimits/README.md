# Assimalign.Cohesion.Http.RequestLimits

Per-request limit features for the Cohesion HTTP server: a typed, per-exchange
max-request-body-size feature (`IHttpMaxRequestBodySizeFeature`) attached at request-parse time
through the core `IHttpRequestInterceptor` seam — observe the effective cap anywhere, adjust it
per request before the body is read, with the wire-level enforcement staying in the transport.

- `docs/OVERVIEW.md` — purpose, dependencies, usage
- `docs/DESIGN.md` — design rationale and migration plan
