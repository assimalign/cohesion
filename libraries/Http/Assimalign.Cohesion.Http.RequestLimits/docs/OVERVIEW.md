# Assimalign.Cohesion.Http.RequestLimits — Overview

Per-request limit features for the Cohesion HTTP server. Ships the typed
`IHttpMaxRequestBodySizeFeature`, `IHttpMinRequestBodyDataRateFeature` (#1383) and
`IHttpMinResponseDataRateFeature` (#1384) — per-exchange views over the request body-size cap and the
minimum request-body and response data rates the server transport enforces — attached by a parse-time `IHttpExchangeInterceptor` to every request. All
three protocol parse paths (HTTP/1.1, HTTP/2, HTTP/3) invoke the interceptor seam (#819), so the
feature is present regardless of protocol, and all three enforce the wire-level cap (see
`docs/DESIGN.md`, "Protocol coverage").

- **Depends on:** `Assimalign.Cohesion.Http` (core) only. The server transport
  (`Assimalign.Cohesion.Http.Connections`) never references this package; it invokes the
  interceptor through the core `IHttpExchangeInterceptor` seam.
- **Registered by:** the composition root (for example `Assimalign.Cohesion.Web.Hosting`, which
  installs it by default) via `HttpRequestLimitsInterceptor.Create()` on the listener options, which
  attaches all three features (the body-size cap, the request-body rate and the response rate) on one
  object (`HttpMaxRequestBodySizeInterceptor.Create()` attaches the body-size feature alone).
- **Consumed by:** endpoints and middleware via `context.MaxRequestBodySize` (an
  `IHttpContext` extension member) or `Features.Get<IHttpMaxRequestBodySizeFeature>()` — read
  the effective cap any time; adjust it until the transport starts reading the body.
- **Disabling the rate for one request:** an endpoint whose body legitimately idles (a
  client-streaming call) sets `context.MinRequestBodyDataRate!.MinDataRate = null` before it reads
  the body; every other request keeps the listener's rate.
- **Disabling the slow-read check for one response:** an endpoint whose reader pauses for long
  stretches sets `context.MinResponseDataRate!.MinDataRate = null` before its response starts.

See `docs/DESIGN.md` for the seams-vs-features rationale, the write-through model, and how the
transport integration is wired (including the move from the original in-core placement).
