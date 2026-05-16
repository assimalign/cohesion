# Assimalign.Cohesion.Web.Authentication &mdash; Design

## Design intent

The familiar property-style `context.User` access from ASP.NET Core,
restored on top of a wire-level HTTP protocol core that does not know
about identity. The package is intentionally narrow: one feature
contract, one default implementation, one extension property.

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

## Family map

| Package | Role | Dependencies |
|---------|------|---------------|
| `Assimalign.Cohesion.Web.Authentication` | Authenticated principal feature + extension property | `Assimalign.Cohesion.Http` |
| Future: `&hellip;Web.Authentication.Cookie` | Cookie-scheme handler | This package |
| Future: `&hellip;Web.Authentication.Bearer` | Bearer-token scheme handler | This package |

Dependency direction is one-way: scheme handlers depend on this
package; this package depends on the protocol core. The protocol core
never gains a back-reference.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited from the shared
build targets. The package contains no reflection, no runtime code
generation, and no dynamic type loading. The feature lookup uses
`typeof(IHttpAuthenticationFeature)` as a JIT-time constant key into a
`Dictionary<Type, object>`; trim and AOT roots are unaffected.

`ClaimsPrincipal` itself is AOT-safe in .NET 10; no special
serialization or runtime-policy surface is consumed by this package.

## Non-goals

- **Authentication scheme implementations.** Cookie, Bearer, and other
  scheme handlers belong in dedicated packages. This package only
  defines the per-request principal surface.
- **Authorization.** Policy evaluation, role checks, and requirement
  handlers live in `Assimalign.Cohesion.Web.Authorization`.
- **Wire-level identity.** TLS client certificates, mutual-TLS
  identity, and HTTP `Authorization` header parsing remain in the
  transport / protocol layers; this package only consumes the result.
- **A claim-transformation pipeline.** Middleware that needs to
  reshape claims should mutate the principal it installs; the feature
  itself stays a passive holder.
