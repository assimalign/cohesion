# Assimalign.Cohesion.Web.HostFiltering — Design

## Design intent

Allowed-hosts enforcement (issue #781): a defense against Host-header injection
(cache poisoning, password-reset poisoning, absolute-URL generation against an
attacker-chosen host), which matters here because a Cohesion web application is
designed to be directly exposed by its own server, not assumed to sit behind a
validating proxy.

The package is deliberately thin: the *matching* primitives — the
`host[:port]` component split on `HttpHost` and the precompiled allowlist
matcher `HttpHostMatcher` — live in `Assimalign.Cohesion.Http` (see its
`docs/DESIGN.md`, "Host values and allowlist matching"), because they are typed
protocol value logic every consumer must agree on. This package owns only the
Web-pipeline surface: the options object, the `UseHostFiltering` verb, and the
internal middleware.

## Why a feature package, not hosting-injected middleware

The first iteration of #781 placed the enforcement inside `Web.Hosting`
(builder options + a first-position wrapper injected at pipeline build), on the
reading that a host guard is server policy. The owner redirected this
(2026-07-16): host filtering is a **feature library** like every other
pipeline concern, and `Web.Hosting` must not carry feature knowledge — the
same hosting-isolation pressure that moved the authentication builder verbs
out of hosting, and the same shape `Web.ForwardedHeaders` shipped with ("every
feature, including foundational middleware, ships as its own `Web.<Feature>`
package with its verb"). The issue's original "no standalone
Web.HostFiltering project" note is superseded by that direction.

The consequence is the composition model changing from *hosting-guaranteed*
first position to a **registration-order contract**: `UseHostFiltering` is
documented (and tested) as *register it first*. This follows the Web area's
middleware-first direction — the application owns its pipeline order — and it
resolves the forwarded-headers ordering question by putting the decision where
it belongs (see below). What was kept from the first iteration:

- **Builder-time compilation.** The allowlist compiles into an
  `HttpHostMatcher` exactly once, inside the `UseHostFiltering` call. Invalid
  patterns throw `ArgumentException` at registration — configuration errors
  surface at startup, never as per-request behavior. Registration is
  dependency-free per the Web-area rule: no service container, no
  configuration binding, no request-time service location.
- **Explicit opt-in.** Not calling the verb means no filtering (the pre-#781
  behavior, zero overhead). Calling it demands a non-empty allowlist — an
  empty one would compile to deny-all and is treated as a configuration error;
  pass `*` to accept any host while keeping the empty-host policy enforced.
- **The request-time check.** The transports already resolve the effective
  host with the correct per-version precedence (HTTP/1.1 absolute/authority-
  form target supersedes `Host` per RFC 9112 §3.2.2; HTTP/2 / HTTP/3
  `:authority` via `HttpFieldNormalization.ResolveAuthority`), so the
  middleware reads `IHttpRequest.Host` and performs one component split plus
  span comparisons. A mismatch answers `400 Bad Request` with an empty body
  (the HTTP/1.1 writer synthesizes `Content-Length: 0`) and short-circuits;
  the connection itself is left alive.

**Why the 400 has no body:** a richer problem-details payload would drag a
`Web.ProblemDetails` dependency into a guard that should stay minimal;
rejected-request bodies are an application error-handling concern (the #864
`OnError` direction), not this package's.

**Empty/missing host policy (RFC 9112 §3.2).** An HTTP/1.1 request that lacks
a `Host` header (and carries no target authority) resolves to
`HttpHost.Empty` and cannot be validated, so it is rejected by default.
`AllowEmptyHost = true` is the explicit opt-out for legacy HTTP/1.0-style
clients; it admits only the *hostless* case — a present-but-unmatched host is
still rejected.

## Validation, not selection — composing with #788

Host filtering **validates** the request ("is this host one of mine?");
routing's host constraints (#788, `RequireHost` / `RouteHostMetadata`)
**select** among endpoints ("which route serves this host?"). They are
complementary, not duplicates: both consume the same `HttpHost` component
semantics from the Http core (bracket-insensitive IPv6, case-insensitive,
apex-excluded `*.` wildcards), so a given wire value means the same thing on
both paths — but a filtering mismatch is a 400, while a routing host mismatch
merely skips a route candidate. Use filtering to bound the hosts the
application answers *at all*, and host-constrained routes to fan traffic
across the hosts inside that boundary.

## Ordering — forwarded headers (`Web.ForwardedHeaders`, #778 / PR #892)

The forwarded-headers middleware's output is **a feature, never mutation**: its
trust walk publishes `IHttpForwardedFeature` (read through the `Effective*`
convention in `Http.Forwarded`) and never rewrites `IHttpRequest.Host`. Two
consequences for composition:

- **This guard always validates the wire host** — behind a proxy, the
  authority the proxy actually dialed — regardless of where it sits relative
  to `UseForwardedHeaders`. Allowlist the name(s) the transport really
  receives. Because the filter consumes nothing from the forwarded feature,
  the two registrations are order-independent today; keeping
  `UseHostFiltering` at the very front simply makes rejection cheapest (no
  trust walk for a request that is about to 400).
- **The forwarded (public) host is deliberately out of this guard's scope.**
  The forwarded walk shape-checks `host` assertions but, by its own design,
  leaves *which* hosts are acceptable "a consumer allowlist concern". If
  validating `EffectiveHost` against an allowlist becomes a real need, it is
  an explicit future knob on this package (and would require registration
  after `UseForwardedHeaders`) — not something that happens implicitly. This
  mirrors ASP.NET's split, where forwarded-host acceptance
  (`ForwardedHeadersOptions.AllowedHosts`) is configured separately from host
  filtering.

## AOT posture

Options → precompiled matcher at registration; request-time span comparisons
only. No reflection, no configuration binding, no service location.

## Non-goals

- **No hosting integration.** The package must not (and cannot, per the
  build-enforced hosting-isolation rule) be referenced by `Web.Hosting`;
  first-position placement is the application's registration-order
  responsibility, not a hosting guarantee.
- **No port-aware allowlisting** — host validation is host-identity; which
  ports are served is a listener/binding concern.
- **No response body on rejection** — see above; applications own error
  payloads.
- **No per-route filtering** — bounding *which* hosts an endpoint serves is
  routing's `RequireHost`, not an allowlist concern.

## Testing

`tests/HostFilteringTests.cs` drives the middleware end to end through
`Assimalign.Cohesion.Web.Testing`'s factory: allow/deny over origin-form
HTTP/1.1 and HTTP/2 `:authority`, registration-order short-circuiting,
case-insensitivity, request-port ignoring, wildcard depth/apex/lookalikes,
IPv6 literals, the `*` pattern, and registration-time failure for invalid or
empty allowlists. Raw HTTP/1.1 exchanges over the in-memory transport cover
what `HttpClient` cannot produce: a missing `Host` header (the empty-host
policy both ways) and absolute-form request-targets superseding the `Host`
header (RFC 9112 §3.2.2). The pattern grammar itself is unit-tested with the
matcher in `Assimalign.Cohesion.Http`.
