# Assimalign.Cohesion.Web.Authentication

Authenticated-principal surface for the Cohesion HTTP family.

## Why a separate package

The Cohesion HTTP protocol core (`Assimalign.Cohesion.Http`) defines only
wire-level concepts &mdash; request, response, headers, methods, status,
targets, body streams. An authenticated `ClaimsPrincipal` is an
**application-layer** concept: it never appears on the wire, it presupposes
a `System.Security.Claims` dependency the protocol core deliberately
avoids, and most protocol-only consumers (`HttpClient` wrappers, reverse
proxies, edge caches, observability layers, the DNS-over-HTTPS transport,
&hellip;) have no use for it.

Keeping authentication in a separate package means the protocol core stays
small, AOT-friendly, and free of identity assumptions. The familiar
property-style `context.User` access is restored here through a .NET 10
extension property, backed by an `IHttpAuthenticationFeature` stored in
`IHttpContext.Features`.

## Surface

| Type | Role |
|------|------|
| `IHttpAuthenticationFeature` | Per-exchange authentication state &mdash; just the current `ClaimsPrincipal` |
| `HttpAuthenticationFeature` | Default in-memory implementation (internal; constructed via the `User` setter) |
| `HttpContextAuthenticationExtensions` | The `context.User` extension property |

## Usage

```csharp
using System.Security.Claims;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;

// Authentication middleware attaches the principal:
ClaimsIdentity identity = new("Bearer");
identity.AddClaim(new Claim(ClaimTypes.Name, "alice"));
context.User = new ClaimsPrincipal(identity);

// Anywhere downstream:
ClaimsPrincipal user = context.User;
if (user.Identity?.IsAuthenticated == true)
{
    // ...
}
```

When no authentication middleware has run, `context.User` returns an empty
`ClaimsPrincipal` (no identity, no claims) instead of `null` &mdash;
matching the ASP.NET Core `HttpContext.User` default so call sites do not
need a null-check on the common path.

## Implementing a custom feature

`HttpAuthenticationFeature` is internal. Middleware that needs richer
authentication state (e.g. authentication-result metadata, ticket
properties) should implement `IHttpAuthenticationFeature` directly and
attach it via `context.Features.Set<IHttpAuthenticationFeature>(...)`. The
`context.User` getter consults the feature collection for any
implementation, not just the package's default.
