# Assimalign.Cohesion.Http.RequestLimits — Overview

Per-request limit features for the Cohesion HTTP server. Ships the typed
`IHttpMaxRequestBodySizeFeature` — a per-exchange view over the request body-size cap the server
transport enforces — attached by a parse-time `IHttpRequestInterceptor` to every request. All
three protocol parse paths (HTTP/1.1, HTTP/2, HTTP/3) invoke the interceptor seam (#819), so the
feature is present regardless of protocol; the wire-level cap enforcement itself remains
HTTP/1.1-only (see `docs/DESIGN.md`, "Protocol coverage").

- **Depends on:** `Assimalign.Cohesion.Http` (core) only. The server transport
  (`Assimalign.Cohesion.Http.Connections`) never references this package; it invokes the
  interceptor through the core `IHttpRequestInterceptor` seam.
- **Registered by:** the composition root (for example `Assimalign.Cohesion.Web.Hosting`, which
  installs it by default) via `HttpRequestLimits.CreateMaxRequestBodySizeInterceptor()` on the
  listener options.
- **Consumed by:** endpoints and middleware via `context.MaxRequestBodySize` (an
  `IHttpContext` extension member) or `Features.Get<IHttpMaxRequestBodySizeFeature>()` — read
  the effective cap any time; adjust it until the transport starts reading the body.

See `docs/DESIGN.md` for the seams-vs-features rationale, the write-through model, and how the
transport integration is wired (including the move from the original in-core placement).
