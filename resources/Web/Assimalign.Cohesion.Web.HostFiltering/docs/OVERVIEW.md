# Assimalign.Cohesion.Web.HostFiltering — Overview

Allowed-hosts enforcement for the Cohesion Web pipeline: a guard against
Host-header injection (cache poisoning, password-reset poisoning, absolute-URL
generation against an attacker-chosen host). It matters because a Cohesion web
application is designed to be directly exposed by its own server, not assumed
to sit behind a validating proxy — ASP.NET ships the equivalent (AllowedHosts)
enabled by default.

## What it provides

- `UseHostFiltering(Action<HostFilteringOptions> configure)` — a pipeline verb
  on `IWebApplicationPipelineBuilder` that registers the validation middleware.
  **Register it first**: registration order is pipeline order, and a request
  whose host fails validation should be rejected before anything else sees it.
- `HostFilteringOptions` — the allowlist (`AllowedHosts`) plus the explicit
  RFC 9112 §3.2 empty/missing-Host policy (`AllowEmptyHost`, default
  `false` = reject).

The matching itself is the `Assimalign.Cohesion.Http` host toolkit:
`HttpHost.TryGetComponents` (the `host[:port]` component split) and
`HttpHostMatcher` (the precompiled allowlist — exact hosts, `*.example.com`
wildcard subdomains with the apex excluded, `*` match-any). The allowlist
compiles exactly once, inside the `UseHostFiltering` call; invalid patterns and
an empty allowlist throw at registration, never at request time.

## Usage

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder();
WebApplication app = builder.Build();

app.UseHostFiltering(options =>
{
    options.AllowedHosts.Add("example.com");
    options.AllowedHosts.Add("*.example.com");
});

// ...the rest of the pipeline...
```

A request whose transport-resolved host (HTTP/1.1 request-target/`Host`
precedence, HTTP/2 / HTTP/3 `:authority`) does not match answers
`400 Bad Request` with an empty body and short-circuits.

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline abstractions the verb extends.
- `Assimalign.Cohesion.Http` — `HttpHost` and `HttpHostMatcher`.

Delivered to applications through the `App.Web` shared framework (via
`Sdk.Web`); no project wiring required. See `docs/DESIGN.md` for the design
decisions, the composition with host-based route matching, and the ordering
interaction with forwarded-headers processing.
