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
Web-pipeline surface: the options object, the `HostFilteringBuilder` behind the
generated `builder.Services.AddHostFiltering` registration verb, the
`UseHostFiltering` verb that places what it registered, and the internal
middleware.

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
documented (and tested) as *place it at the front*; the area's
[middleware order](../../../../docs/resources/Web/MIDDLEWARE_ORDER.md) gives its exact place. This follows the Web area's
middleware-first direction — the application owns its pipeline order — and it
resolves the forwarded-headers ordering question by putting the decision where
it belongs (see below). What was kept from the first iteration:

- **Builder-time compilation.** The allowlist compiles into an
  `HttpHostMatcher` exactly once, inside the `AddHostFiltering` call
  (`HostFilteringBuilder.Build`). Invalid patterns throw `ArgumentException`
  at registration — configuration errors surface at startup, never as
  per-request behavior. The package stays dependency-free per the Web-area
  rule: the registration verb is generated into the application, and the
  package references no service container, no configuration binding, and does
  no request-time service location.
- **Explicit opt-in.** Not using host filtering means no filtering (the
  pre-#781 behavior, zero overhead). Registering it demands a non-empty
  allowlist — an empty one would compile to deny-all and is treated as a
  configuration error; register `*` to accept any host while keeping the
  empty-host policy enforced.
- **The request-time check.** The transports already resolve the wire host
  with the correct per-version precedence (HTTP/1.1 absolute/authority-form
  target supersedes `Host` per RFC 9112 §3.2.2; HTTP/2 / HTTP/3 `:authority`
  via `HttpFieldNormalization.ResolveAuthority`), and the forwarded-headers
  middleware publishes a proxy-forwarded host as a typed value too. The
  middleware reads the effective host (`context.EffectiveHost`, see "Validating
  the forwarded host" below) and performs one feature lookup, one component
  split, and span comparisons. A mismatch answers `400 Bad Request` with an
  empty body (the HTTP/1.1 writer synthesizes `Content-Length: 0`) and
  short-circuits; the connection itself is left alive.

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

Both read the same host: filtering validates the *effective* host (below), and
routing's host constraints match it too (#1077). Behind a proxy that rewrites
`Host` and forwards the original in `X-Forwarded-Host`, the host the allowlist
admits is therefore the host the routes select on, never the upstream
authority the proxy dialed. Until #1077 routing matched the wire host, so the
two disagreed behind such a proxy.

## Validating the forwarded host (`Web.ForwardedHeaders`, #778; #1050)

The forwarded-headers middleware's output is **a feature, never mutation**: its
trust walk publishes `IHttpForwardedFeature` and never rewrites
`IHttpRequest.Host`. Consumers read the result through the `Effective*`
convention in `Http.Forwarded` (owner decision 3 in
`docs/programs/HTTP_WEB_PROGRAM_PLAN.md` §7.4). This guard is one of those
consumers: **it validates `context.EffectiveHost`**, which is

- the host a trusted proxy forwarded, when `UseForwardedHeaders` ran first and
  accepted a hop that carried one, and
- the transport-resolved wire host otherwise — no forwarded-headers
  middleware, an untrusted peer, a hop that forwarded no host, or a
  placement that puts this guard ahead of `UseForwardedHeaders`.

**Why the effective host.** The forwarded walk shape-checks `host` assertions
but leaves *which* hosts are acceptable to a consumer allowlist. The effective
host is the one every downstream consumer reads — HTTPS redirection's
`Location`, absolute-URL generation, output-cache keys — so it is the value
Host-header injection would poison and the value the allowlist must bound.
Validating only the wire host behind a proxy would check the authority the
proxy dialed (usually a fixed internal name) while an attacker-influenced
forwarded host flowed downstream unchecked. The earlier position — "forwarded
host out of scope, a future knob" — was replaced by this default rather than a
knob: without the forwarded-headers middleware the effective host *is* the wire
host, so no proxy-less deployment sees a change.

**Trust dependency.** The guard never reads `Forwarded`/`X-Forwarded-Host`
itself. A forwarded host reaches it only through the forwarded-headers trust
model (`KnownProxies`/`KnownNetworks`, `ForwardLimit`, header selection), so a
client that asserts `X-Forwarded-Host` directly is validated on its wire host.
The trust model decides *whose* host assertion is believed; this guard decides
whether the believed host is one the application serves.

**Ordering.** Place `UseHostFiltering` directly after `UseForwardedHeaders`
(only `UseHttpLogging` and `UseSecurityHeaders`, which read no client identity,
go ahead of both; the area's [middleware order](../../../../docs/resources/Web/MIDDLEWARE_ORDER.md) gives the
full sequence), and allowlist the **public** names clients use. Placed
the other way round, the guard runs before the feature exists and validates
the wire host — a supported, deliberate configuration for deployments that want
to bound the upstream authority instead, but then the forwarded host is not
bounded by this guard. This composition differs from ASP.NET, whose
forwarded-headers middleware rewrites `Request.Host` in place (so host filtering
registered after it validates the forwarded host implicitly) and which also
offers a separate forwarded-host allowlist
(`ForwardedHeadersOptions.AllowedHosts`); here the rewrite is replaced by the
effective read, and the one allowlist covers both cases.

## Registration: `AddHostFiltering` configures, `UseHostFiltering` places (2026-10-28)

`builder.Services.AddHostFiltering(hosts => ...)` is a generated component
integration over `HostFilteringBuilder`. It builds the middleware once, which
compiles `HostFilteringBuilder.Options.AllowedHosts` into the matcher and so
rejects an empty allowlist or an invalid pattern with `ArgumentException`
where the application registers it, and registers the middleware as a
container-owned `IWebApplicationMiddleware` singleton. The middleware keeps the
compiled matcher and the empty-host flag, so changing the options afterwards
has no effect. The `(services, hosts) => ...` overload builds when the
middleware is first resolved, as the pipeline is composed, so its
misconfiguration fails the start instead of the registration.

`UseHostFiltering()` takes no arguments: it places the last registration of
the middleware it finds in `IWebApplicationContext.Middleware`. There is no
default allowlist to fall back to — filtering every request against nothing
would deny all of them, and accepting every host would make the verb a no-op —
so placing it without a registration fails the pipeline composition with an
`InvalidOperationException` that names `AddHostFiltering`, the same shape as
`UseForwardedHeaders`. *Rejected:* the former
`UseHostFiltering(Action<HostFilteringOptions> configure)`; a configuration
callback on the pipeline verb split the feature's settings across two call
sites and kept them out of the registrations the host enumerates.

## AOT posture

Options → precompiled matcher at registration; request-time span comparisons
only. No reflection, no configuration binding, no service location.

## Diagnostics

The internal `HostFilteringEventSource` is named
`Assimalign.Cohesion.Web.HostFiltering`. It reports only rejected requests, at the
middleware's single rejection branch after the effective-host allowlist decision.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | `RequestRejected` | Verbose | `reason` (`EmptyHost` or `HostNotAllowed`), `host`, `forwarded:bool`, `requestId` |

`host` is the untrusted host component, structurally separated from its port by
`HttpHost.TrySplitHostPort` after trimming HTTP optional whitespace (SP and HTAB).
The split does not validate the port: `denied.example:999999` and
`denied.example:invalid` both report `denied.example`, and `[::1]:999999` reports
`::1`. `HttpHost.Host` is unsuitable here because a malformed port makes it return
the raw authority. Splitting, truncation to 253 characters and string allocation
all happen inside `IsEnabled`. The payload is empty for `EmptyHost` or when the
authority cannot be structurally split, such as `denied.example:` or `[::1`.
`forwarded` means the forwarded feature accepted at least one hop. No path, query,
raw header value or
route template is written. The request id is read from `IWebRequestIdFeature`
inside `IsEnabled`, with an empty string when a custom server omits the feature.
`Activity.Current` is never used. The `Web.Server` reference supplies
that feature and is already part of App.Web's closure.

The `rejected-requests` total is a lazily created `PollingCounter`, backed by an
interlocked increment for every rejection even without a listener. Accepted requests
raise nothing and do not change the counter. No throttling is needed for Verbose
events. The serial `HostFilteringEventSourceTests` verify the strict manifest,
request correlation, empty and forwarded hosts, truncation, malformed ports and
unsplittable authorities over raw HTTP/1.1, counter publication, and zero allocation
for disabled diagnostics. Trusted-forwarding tests also cover malformed ports.

Compared with 10.0.0-preview.1, this adds an opt-in diagnostic signal and exact
counter, with no public API or host-acceptance change. Preview.1's rejection branch
was silent; this observability gap does not itself bypass host validation.
The malformed-port correction changes only the unreleased event payload; the source
did not exist in 10.0.0-preview.1, so it adds no breaking change against that release.

## Non-goals

- **No hosting integration.** `Web.Hosting` does not reference the package
  (the build-enforced hosting-isolation rule forbade it until its 2026-10-09
  relaxation); front-of-pipeline placement is the application's registration-order
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
empty allowlists (a bare `AddHostFiltering()` included). Every case registers
through the generated `AddHostFiltering` verb; placement is covered by the last
of two registrations winning, the start failure without a registration, and
the service-provider overload (its allowlist applied, and an empty one failing
the start rather than the registration). Raw HTTP/1.1 exchanges over the
in-memory transport cover what `HttpClient` cannot produce: a missing `Host`
header (the empty-host policy both ways) and absolute-form request-targets
superseding the `Host` header (RFC 9112 §3.2.2). The pattern grammar itself is unit-tested with the
matcher in `Assimalign.Cohesion.Http`.

`tests/HostFilteringForwardedTests.cs` composes the real forwarded-headers
middleware (the test project references `Web.ForwardedHeaders`, registered with
its own generated `AddForwardedHeaders` verb) in front of the guard over the
same factory, whose in-memory peer is trusted as a local transport — the
simulated proxy. It pins that an allowlisted forwarded host is accepted even
when the wire host is not, that a forwarded host outside the allowlist is
rejected even when the wire host is allowlisted, and that without the
forwarded-headers middleware, or with the guard placed ahead of it, the wire
host is validated and forwarding headers are ignored.

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.
