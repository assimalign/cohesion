# Assimalign.Cohesion.Web.HttpsPolicy — Overview

HTTPS policy for the Cohesion Web pipeline: the natural pair of HTTP-to-HTTPS
redirection and HTTP Strict Transport Security (HSTS), delivered as one lean
feature package. Both are table-stakes for an enterprise-facing web server, and
both hinge on the same question — *is this connection secure?* — answered from
the transport-derived typed scheme the Web TLS surface (#763) resolves onto
every request.

## What it provides

- `UseHttpsRedirection(Action<HttpsRedirectionOptions>?)` — a pipeline verb on
  `IWebApplicationPipelineBuilder`. An insecure request is answered with a
  bodyless, method-preserving redirect (`307` by default, `308` when configured)
  whose `Location` is the same request re-addressed to `https`, the request host
  with its port replaced by the configured HTTPS port, and the path and query
  preserved. An already-secure request passes straight through. **Register it
  early** so an insecure request is discarded before downstream middleware works
  on a response that is about to be thrown away.
- `UseHsts(Action<HstsOptions>?)` — a pipeline verb that emits the
  `Strict-Transport-Security` field, composed once at registration, on secure
  responses **only** (RFC 6797 §7.2), skipping the excluded hosts (`localhost`,
  `127.0.0.1`, `[::1]` by default).

Both verbs take an optional configuration callback (the defaults are sensible)
and validate their options at builder time — a bad status, port, `max-age`, or
excluded-host pattern throws at registration, never per request.

## Usage

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder();
WebApplication app = builder.Build();

// Earliest: upgrade insecure requests before anything else runs.
app.UseHttpsRedirection(options =>
{
    options.HttpsPort = 8443;                       // default 443 (omitted from Location)
    options.StatusCode = HttpStatusCode.PermanentRedirect; // default 307
});

// Before the exception boundary: the policy must survive a reset error response.
app.UseHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);        // the default
    options.IncludeSubDomains = true;
});

app.UseErrorHandling();
// ...the rest of the pipeline...
```

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline abstractions the verbs extend.
- `Assimalign.Cohesion.Http` — `HttpScheme` (the transport-derived security
  signal), `HttpHost`/`HttpHostMatcher` (excluded-host matching), `HttpHeaderKey`
  (the RFC 6797 `Strict-Transport-Security` key), and the redirect status codes.

No DI, configuration, or logging dependency — the verbs capture values at builder
time and the middleware resolves nothing per request. Delivered to applications
through the `App.Web` shared framework (via `Sdk.Web`); no project wiring
required. See `docs/DESIGN.md` for the security-detection, port-resolution, and
HSTS emission-point decisions, and the non-goals.
