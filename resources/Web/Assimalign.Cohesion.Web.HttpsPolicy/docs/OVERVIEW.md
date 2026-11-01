# Assimalign.Cohesion.Web.HttpsPolicy — Overview

HTTPS policy for the Cohesion Web pipeline: the natural pair of HTTP-to-HTTPS
redirection and HTTP Strict Transport Security (HSTS), delivered as one lean
feature package. Both are table-stakes for an enterprise-facing web server, and
both hinge on the same question — *is this connection secure?* — answered from
the effective typed scheme: the scheme a trusted proxy asserted when
`UseForwardedHeaders` resolved one, otherwise the transport-derived scheme the
Web TLS surface (#763) resolves onto every request.

## What it provides

- `AddHttpsRedirection(redirection => ...)` and `UseHttpsRedirection()` — the registration
  verb on `builder.Services` configures the redirect (`HttpsRedirectionBuilder.Options`), and the
  pipeline verb places it. An insecure request is answered with a
  bodyless, method-preserving redirect (`307` by default, `308` when configured)
  whose `Location` is the same request re-addressed to `https`, the request host
  with its port replaced by the configured HTTPS port, and the path and query
  preserved, percent-encoded. A request whose host the redirect cannot name (empty,
  malformed, or carrying a character such as `/` or `@`) is answered with `400`
  instead. An already-secure request passes straight through. **Register it
  early** so an insecure request is discarded before downstream middleware works
  on a response that is about to be thrown away.
- `AddHsts(hsts => ...)` and `UseHsts()` — the registration verb configures the policy
  (`HstsBuilder.Options`), and the pipeline verb places the middleware that emits the
  `Strict-Transport-Security` field, composed once at registration, on secure
  responses **only** (RFC 6797 §7.2), skipping the excluded hosts (`localhost`,
  `127.0.0.1`, `[::1]` by default).

Configuration lives on the `Add` verbs only ("Add configures, Use places"). Each `Use` verb takes
no arguments: it places the middleware its `Add` verb registered, or one with the defaults when the
application registered none. A bad status, port, `max-age`, or excluded-host pattern throws where it
is registered, never per request.

## Usage

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder();

// Behind a TLS-terminating proxy only: resolve the client-facing scheme and host first,
// so redirection recognizes the proxy's https instead of redirecting in a loop.
builder.Services.AddForwardedHeaders(forwarded => forwarded.Options.Headers = ForwardedHeaderNames.XForwarded);
builder.Services.AddHttpsRedirection(redirection =>
{
    redirection.Options.HttpsPort = 8443;                           // default 443 (omitted from Location)
    redirection.Options.StatusCode = HttpStatusCode.PermanentRedirect; // default 307
});
builder.Services.AddHsts(hsts => hsts.Options.IncludeSubDomains = true); // 365-day max-age by default

WebApplication app = builder.Build();
app.UseForwardedHeaders();

// Next, after UseHostFiltering when that is registered: upgrade insecure requests before
// anything else runs.
app.UseHttpsRedirection();

// Before the exception boundary: the policy must survive a reset error response.
app.UseHsts();

app.UseErrorHandling();
// ...the rest of the pipeline...
```

## Recognizing a redirect loop

Behind a TLS-terminating proxy, a `307` or `308` on every route and clients that
never reach the page indicate a redirect loop. Check HTTP access logs and the
status breakdown in `http.server.request.duration`. No event reports the loop
when `UseForwardedHeaders` is missing. If that middleware is present but does
not trust the proxy, `ForwardingStopped(UntrustedPeer)` identifies the trust
failure when forwarding headers arrive. Register it before `UseHttpsRedirection`
and configure the actual proxy peers as trusted. See
[Recognizing a redirect loop](DESIGN.md#recognizing-a-redirect-loop) for the
diagnosis and checks.

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline abstractions the verbs extend.
- `Assimalign.Cohesion.Http` — `HttpScheme` (the typed security signal),
  `HttpHost`/`HttpHostMatcher` (excluded-host matching), `HttpHeaderKey` (the
  RFC 6797 `Strict-Transport-Security` key), and the redirect status codes.
- `Assimalign.Cohesion.Http.Forwarded` — the `EffectiveScheme`/`EffectiveHost`
  reads, which fall back to the wire values when no forwarded-headers middleware
  ran.

No DI, configuration, or logging dependency — the verbs capture values at builder
time and the middleware resolves nothing per request. Delivered to applications
through the `App.Web` shared framework (via `Sdk.Web`); no project wiring
required. See `docs/DESIGN.md` for the security-detection, port-resolution, and
HSTS emission-point decisions, and the non-goals.

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.
