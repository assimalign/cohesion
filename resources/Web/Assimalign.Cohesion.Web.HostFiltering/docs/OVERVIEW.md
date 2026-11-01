# Assimalign.Cohesion.Web.HostFiltering — Overview

Allowed-hosts enforcement for the Cohesion Web pipeline: a guard against
Host-header injection (cache poisoning, password-reset poisoning, absolute-URL
generation against an attacker-chosen host). It matters because a Cohesion web
application is designed to be directly exposed by its own server, not assumed
to sit behind a validating proxy — ASP.NET ships the equivalent (AllowedHosts)
enabled by default.

## What it provides

- `AddHostFiltering(hosts => ...)` and `UseHostFiltering()` — the registration
  verb on `builder.Services` configures the allowlist
  (`HostFilteringBuilder.Options`) and compiles it, and the pipeline verb on
  `IWebApplicationPipelineBuilder` places the validation middleware.
  **Place it at the front**: the order of the `Use` verbs is pipeline order, and a
  request whose host fails validation should be rejected before anything else
  does work for it. Behind a proxy it goes directly after `UseForwardedHeaders`,
  because it validates the effective host. Only `UseHttpLogging` and
  `UseSecurityHeaders`, which read no client identity on the way in, run ahead
  of both (the area's [middleware order](../../../../docs/resources/Web/MIDDLEWARE_ORDER.md)).
- `HostFilteringOptions` — the allowlist (`AllowedHosts`) plus the explicit
  RFC 9112 §3.2 empty/missing-Host policy (`AllowEmptyHost`, default
  `false` = reject).

The matching itself is the `Assimalign.Cohesion.Http` host toolkit:
`HttpHost.TryGetComponents` (the `host[:port]` component split) and
`HttpHostMatcher` (the precompiled allowlist — exact hosts, `*.example.com`
wildcard subdomains with the apex excluded, `*` match-any). The allowlist
compiles exactly once, inside the `AddHostFiltering` call; invalid patterns and
an empty allowlist throw at registration, never at request time.

Configuration lives on the `Add` verb only ("Add configures, Use places").
`UseHostFiltering()` takes no arguments: it places the middleware
`AddHostFiltering` registered (the last registration when there are several).
There is no default allowlist, so when the application registered none,
composing the pipeline fails with an `InvalidOperationException` that names
`AddHostFiltering`.

## Usage

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.Services.AddHostFiltering(hosts =>
{
    hosts.Options.AllowedHosts.Add("example.com");
    hosts.Options.AllowedHosts.Add("*.example.com");
});

WebApplication app = builder.Build();
app.UseHostFiltering();

// ...the rest of the pipeline...
```

A request whose effective host does not match answers `400 Bad Request` with an
empty body and short-circuits. The effective host is the transport-resolved
host (HTTP/1.1 request-target/`Host` precedence, HTTP/2 / HTTP/3
`:authority`), unless the application runs behind a trusted proxy and
places `UseForwardedHeaders` first — then it is the host the proxy
forwarded, and the allowlist should name the public hosts:

```csharp
builder.Services.AddForwardedHeaders(forwarded => forwarded.Options.Headers = ForwardedHeaderNames.XForwarded);
builder.Services.AddHostFiltering(hosts => hosts.Options.AllowedHosts.Add("example.com"));

WebApplication app = builder.Build();
app.UseForwardedHeaders();
app.UseHostFiltering();
```

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline abstractions the `Use` verb extends
  and the middleware contract the `Add` verb registers.
- `Assimalign.Cohesion.Http` — `HttpHost` and `HttpHostMatcher`.
- `Assimalign.Cohesion.Http.Forwarded` — the `EffectiveHost` read, which falls
  back to the transport-resolved host when no forwarded-headers middleware ran.

Delivered to applications through the `App.Web` shared framework (via
`Sdk.Web`); no project wiring required. See `docs/DESIGN.md` for the design
decisions, the composition with host-based route matching, and the ordering
interaction with forwarded-headers processing.

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.
