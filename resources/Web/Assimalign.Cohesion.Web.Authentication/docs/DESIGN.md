# Assimalign.Cohesion.Web.Authentication &mdash; Design

## Design intent

The familiar property-style `context.User` access from ASP.NET Core,
restored on top of a wire-level HTTP protocol core that does not know
about identity, **plus the scheme registration model and per-request
dispatch surface** that concrete scheme handlers (Cookie, Bearer) plug
into.

The package began (before #790) as one feature contract, one default
implementation, and one extension property — the per-request principal
only. Issue #790 grew it into the home for the **authentication scheme
model**: the handler contract, the named-scheme registry with
default-scheme selection, the `ClaimsPrincipal`-based result types, and
the request-time dispatch service. The concrete handlers still live in
their own packages (`…Cookie`, `…Bearer`); this package owns only the
model they share. See "The scheme model" below.

The design is shaped by three constraints that pull in different
directions:

1. The protocol core (`Assimalign.Cohesion.Http`) must stay free of
   `System.Security.Claims` so that protocol-only consumers (HTTP
   clients, reverse proxies, the DNS-over-HTTPS transport, &hellip;) do
   not pay for an application-layer dependency they never use.
2. Application code that does need identity wants `context.User` as a
   property, not `context.GetUser()`. Switching from a property to a
   method on every consumer is a compatibility tax with no payoff.
3. Other features (sessions, parsed forms, future middleware-attached
   state) need to layer on top of the same context without each one
   inventing its own storage convention.

The .NET 10 `extension(...)` property syntax + a strongly-typed feature
collection on `IHttpContext` resolves all three.

## Why a feature, not `IHttpContext.Items`

Sessions and form bodies layer through the loosely-typed
`IHttpContext.Items` dictionary because their natural surface is
already string-keyed (session IDs, form field names). Identity is
different:

- **Type, not name, identifies it.** There is exactly one principal per
  exchange. Keying it by string would invent a magic name that every
  reader has to know and every writer has to spell correctly.
- **Middleware composes it.** Authentication-result metadata, ticket
  properties, and downstream re-authentication all want to mutate the
  same object reference, not look it up by name and risk replacing it.
- **AOT-safe lookup.** `Features.Get<IHttpAuthenticationFeature>()` is
  `typeof(T)`-keyed; no reflection, no string-table lookup, no boxing.

`IHttpContext.Features` &mdash; defined in the protocol core but
otherwise empty by default &mdash; is the strongly-typed counterpart to
`Items`. Identity is the first concrete feature to use it; sessions and
forms continue to use `Items` until they have a reason to migrate.

## Why the feature is minimal (just `User`)

The feature ships with a single `ClaimsPrincipal User { get; set; }`
member. Everything else &mdash; authentication scheme, ticket
properties, expiration, refresh hints &mdash; is deferred to follow-up
PRs that have a concrete consumer driving the shape. Adding members
later is additive and source-compatible; locking in a wide surface up
front based on guesses is not.

This deliberately mirrors ASP.NET Core's
`IHttpAuthenticationFeature` evolution: the original shape was just
`User`, and per-scheme handlers grew in `IAuthenticationService`
sibling features rather than bloating the per-request feature.

## Why the extension property returns an empty principal, not `null`

Every consumer that touches `context.User` would otherwise need a
defensive null-check. Returning a singleton empty `ClaimsPrincipal`
(no identity, no claims) when no `IHttpAuthenticationFeature` is
attached matches the ASP.NET Core `HttpContext.User` default so
existing patterns like `if (context.User.Identity?.IsAuthenticated)`
work unchanged. The empty principal is a static singleton; it costs
nothing to return.

The setter, in contrast, throws on `null` &mdash; setting "no user"
should be done by removing the feature
(`context.Features.Set<IHttpAuthenticationFeature>(null)`), not by
installing a feature with a null `User`. That keeps the invariant
"if a feature exists, its `User` is non-null" cheap to reason about.

## Why the setter reuses the existing feature instance

When `context.User = newPrincipal` runs and a feature is already
installed, the setter mutates the feature's `User` property rather
than installing a fresh `HttpAuthenticationFeature`. Middleware that
captured the feature reference earlier in the pipeline (e.g. for later
re-authentication or scheme-aware decisions) continues to observe the
same instance, which is the behaviour readers expect from an
"in-place" assignment.

The first assignment on a context with no feature installs the
package's internal `HttpAuthenticationFeature`. Custom middleware
that needs a richer feature implementation should install it
directly through the feature collection before any code paths assign
through `context.User`.

## Why there is no `request.User` accessor

Retiring the vestigial `Assimalign.Cohesion.Http.Identity` skeleton
(issue #759) raised the question of whether its one non-defective
trait &mdash; a get-only `request.User` extension on `IHttpRequest`
&mdash; should be absorbed here. It is deliberately **not** added, for
three reasons:

1. **It would be redundant.** `IHttpRequest` already exposes
   `HttpContext`, so `request.HttpContext.User` already resolves the
   principal through the existing `context.User` accessor. A
   `request.User` property would be one-hop sugar over a path that
   already works.
2. **It diverges from the precedent this package mirrors.** ASP.NET
   Core exposes the principal as `HttpContext.User`, not
   `HttpRequest.User`. Adding a request-level accessor would introduce
   a shape callers do not expect from the framework this design
   deliberately tracks.
3. **The surface stays minimal by design.** This package is "one
   feature contract, one default implementation, one extension
   property." A second accessor is additive and can be introduced
   later if a concrete consumer needs it; locking it in now on
   speculation contradicts the same minimalism that shaped the feature
   itself (see "Why the feature is minimal").

Had such an accessor been added, it would use the empty-principal
default like `context.User` &mdash; **never** the retired skeleton's
`ClaimsPrincipal.Current` fallback, which reads ambient thread state
and has no place in a per-exchange model.

## The scheme model

The model added by #790 mirrors the ASP.NET Core authentication shape,
adapted to Cohesion's DI-free, feature-keyed conventions.

**`IAuthenticationHandler` + `IAuthenticationSignInHandler`.** A handler
owns one scheme's wire behavior: `AuthenticateAsync` (read/validate a
credential), `ChallengeAsync` (prompt for one), `ForbidAsync` (deny an
authenticated-but-unauthorized request). Schemes that also establish a
session — the cookie handler — additionally implement
`IAuthenticationSignInHandler` (`SignInAsync`/`SignOutAsync`). The split
means a bearer scheme, which validates a caller-supplied token on every
request and holds no session, cannot be signed into by mistake — the
dispatch service throws instead of silently no-op'ing.

**`AuthenticationScheme` carries a `Func<IAuthenticationHandler>`, not a
type.** Handlers are produced by a factory delegate captured at builder
time, never by reflection or a container `Activator`. The factory closes
over the scheme's already-constructed options (and, for cookies, its
ticket protector), so scheme resolution is allocation-cheap and fully
AOT-safe. This is the key adaptation away from ASP.NET Core, which
resolves handlers from DI by type.

**The result is `ClaimsPrincipal`-based, deliberately.** `AuthenticateResult`
and `AuthenticationTicket` carry a `System.Security.Claims.ClaimsPrincipal`,
matching `context.User` and the rest of the Web pipeline — *not*
IdentityModel's `IIdentitySubject`. The two identity models meet in
exactly one place: the bearer handler's mapper, which projects a
validated JWT onto a principal. Keeping the Web layer in `ClaimsPrincipal`
avoids forcing every middleware and endpoint to learn a second claim
vocabulary. The success/failure-as-values shape (a computed `Succeeded`,
an unconstructible "succeeded-with-failure" state) mirrors the
IdentityModel `TokenValidationResult`/`AuthenticationResult` contracts so
the family reads consistently.

**Dispatch travels as a feature, not a service resolve.**
`IAuthenticationService` extends `IHttpFeature`: it is a builder-time
singleton installed on every request's feature collection (the same
mechanism `RouterFeature` uses). `context.AuthenticateAsync()` and
friends resolve it type-keyed from `context.Features` — there is no
request-time service location. The service reads `AuthenticationOptions`
live, so schemes registered by chained `AddCookie`/`AddJwtBearer` calls
that run *after* the service is created are still resolved; and it caches
each request's handler on `context.Items` so an authenticate followed by
a challenge reuses one initialized instance.

**Result feature alongside the principal feature.**
`IAuthenticationResultFeature` holds the default-scheme `AuthenticateResult`
(ticket, properties, failure) so authorization and diagnostics can inspect
*how* the principal was established, next to `IHttpAuthenticationFeature`
which holds only `context.User`. This mirrors ASP.NET Core's
`IAuthenticateResultFeature`.

**`IApiEndpointMetadata` is the redirect-vs-status seam.** A cookie
challenge on a browser endpoint redirects to a login page; on an API
endpoint it must emit a bare `401`. The decision keys on the endpoint
metadata marker `IApiEndpointMetadata` (mirroring the .NET 10 cookie
handler), resolved reflection-free through the #150 metadata bag surfaced
by `Web.Routing`. The marker is defined here (an empty interface) but read
only by the cookie handler; the bearer handler is always an API scheme and
needs no check.

**Builder-time registration lives in `*.Hosting`.** Per the framework's
layering, this package defines the model and the request-time service but
does *not* wire schemes — `AddAuthentication`/`AddCookie`/`AddJwtBearer`
(which construct the data-protection key ring and read configuration) live
in `Assimalign.Cohesion.Web.Hosting`, the only place DI/config/crypto
composition is sanctioned. `AuthenticationService.Create` and the public
handler factories (`CookieAuthentication.CreateHandler`,
`JwtBearerAuthentication.CreateHandler`) are the seams Hosting uses to
build the model while the concrete handler and service implementations
stay `internal`.

## Family map

| Package | Role | Dependencies |
|---------|------|---------------|
| `Assimalign.Cohesion.Web.Authentication` | Principal feature + scheme model (handler contract, scheme registry, dispatch service, result types) | `Assimalign.Cohesion.Http` |
| `&hellip;Web.Authentication.Cookie` | Cookie-scheme handler (protected ticket, sliding expiration, login/logout) | This package, `Http.Cookies`, `Web.Routing`, `Security.DataProtection` |
| `&hellip;Web.Authentication.Bearer` | Bearer-token scheme handler (JWT validation + signature seam) | This package, `IdentityModel.Token.JsonWebToken` |
| `&hellip;Web.Hosting` | Builder-time registration (`AddAuthentication`/`AddCookie`/`AddJwtBearer`, `UseAuthentication`) + key-ring composition | The three above + `Security.DataProtection` |

Dependency direction is one-way: scheme handlers depend on this
package; this package depends on the protocol core. The protocol core
never gains a back-reference. Only `*.Hosting` depends on the handler
packages, because only the composition root wires them.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited from the shared
build targets. The package contains no reflection, no runtime code
generation, and no dynamic type loading. The feature lookup uses
`typeof(IHttpAuthenticationFeature)` as a JIT-time constant key into a
`Dictionary<Type, object>`; trim and AOT roots are unaffected.

`ClaimsPrincipal` itself is AOT-safe in .NET 10; no special
serialization or runtime-policy surface is consumed by this package.

## Non-goals

- **Concrete scheme handlers.** This package defines the scheme *model*
  (the handler contract, the registry, the dispatch service); the actual
  Cookie and Bearer handlers live in dedicated packages, and any other
  scheme (API key, mutual-TLS) is a further package or a custom
  `IAuthenticationHandler`. This package ships no handler.
- **Builder-time wiring.** `AddAuthentication`/`AddCookie`/`AddJwtBearer`
  and `UseAuthentication` live in `Web.Hosting`; this package is
  request-path only and free of DI/config/crypto dependencies.
- **OAuth2 / OIDC interactive login.** Authorization-code and other
  redirect-based sign-in flows are follow-ups behind the IdentityModel
  and IdentityHub epics, not part of this scheme model.
- **Authorization.** Policy evaluation, role checks, and requirement
  handlers live in `Assimalign.Cohesion.Web.Authorization`.
- **Wire-level identity.** TLS client certificates, mutual-TLS
  identity, and HTTP `Authorization` header parsing remain in the
  transport / protocol layers; this package only consumes the result.
- **A claim-transformation pipeline.** Middleware that needs to
  reshape claims should mutate the principal it installs; the feature
  itself stays a passive holder.
