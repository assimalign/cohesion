# Assimalign.Cohesion.Web.Authorization — Overview

Endpoint authorization for the Cohesion Web pipeline. The package decides whether the principal a
request authenticated as may reach the endpoint it matched, and answers the requests that may not
with a challenge or a forbid through `Web.Authentication`. It evaluates over the BCL
`ClaimsPrincipal` that `Web.Authentication` establishes (`context.User`).

## Scope

- **Policies** (`AuthorizationPolicy`, built with `AuthorizationPolicyBuilder`): immutable lists of
  requirements a request must all satisfy (an authenticated user, roles, claims with allowed values,
  sync or async delegate assertions, or a custom `IAuthorizationRequirement`), plus the authentication
  schemes that establish the principal they evaluate.
- **Options** (`AuthorizationOptions`, registered with `AddAuthorization`): the default policy
  (an authenticated user), an optional fallback policy for requests without authorization metadata,
  and named policies. They become read-only once registered.
- **Endpoint metadata** (`AuthorizationMetadata`, attached with `RequireAuthorization(...)` and
  `AllowAnonymous()` on routes and groups). Every authorization item on an endpoint applies, except
  those declared before the most specific `AllowAnonymous`, which clears them.
- **The middleware** (`UseAuthorization`), after `UseRouting` and `UseAuthentication`: it computes the
  endpoint's effective policy, authenticates the policy's own schemes when it names any (per-endpoint
  scheme selection), evaluates the policy, and challenges an anonymous caller or forbids an
  authenticated one. A CORS preflight is never authorized.
- **Fail closed.** An endpoint that requires authorization fails at dispatch with
  `InvalidOperationException` when `UseAuthorization` is missing or registered ahead of `UseRouting`,
  instead of running unauthorized.
- **Read access for describers.** `TryGetAuthorizationOptions` on the application context returns the
  registered, read-only options; `AuthorizationOptions.TryGetPolicy` resolves a named policy; and
  `AuthorizationOptions.GetEffectivePolicy` returns the policy `UseAuthorization` applies to an endpoint
  (the computation the middleware itself runs). Web.OpenApi documents security requirements from it.

## Dependencies

- `Assimalign.Cohesion.Web` — the builder, pipeline, and middleware abstractions the verbs build on.
- `Assimalign.Cohesion.Web.Routing` — the published endpoint and its metadata, the convention-builder
  seam the endpoint verbs extend, and the acknowledgement routing checks before it dispatches.
- `Assimalign.Cohesion.Web.Authentication` — `context.User`, and the `AuthenticateAsync`,
  `ChallengeAsync` and `ForbidAsync` verbs the middleware answers through.
- `Assimalign.Cohesion.Http` — the HTTP context.

It never references `Assimalign.Cohesion.Web.Hosting` or any `Assimalign.Cohesion.Hosting*` library
(the resource hosting-isolation rules `COHRES001` and `COHRES004`).

## Usage

```csharp
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Routing;

builder.AddRouting();
builder.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie()
    .AddJwtBearer(options => { /* signing keys, issuers, audiences */ });

builder.AddAuthorization(options =>
{
    options.AddPolicy("admins", policy => policy.RequireRole("admin"));
    options.AddPolicy("sales", policy => policy.RequireClaim("department", "sales", "support"));

    // Optional: authorize every request that has no authorization metadata.
    options.FallbackPolicy = options.DefaultPolicy;
});

WebApplication app = builder.Build();

IRouterBuilder routes = app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet(CookieAuthenticationDefaults.LoginPath, LoginPageAsync).AllowAnonymous();
app.MapGet("/profile", ProfileAsync).RequireAuthorization();          // the default policy
app.MapGet("/admin", AdminAsync).RequireAuthorization("admins");      // a named policy

// An API endpoint that accepts only Bearer tokens, whatever the default scheme accepted.
app.MapGet("/api/orders", OrdersAsync).RequireAuthorization(policy => policy
    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser());

// A group requirement; a route in the group can opt out.
IRouterGroupBuilder staff = app.MapGroup("/staff").RequireAuthorization(policy => policy.RequireRole("employee"));
staff.MapGet("payroll", PayrollAsync).RequireAuthorization("admins"); // employee and admin
staff.MapGet("holidays", HolidaysAsync).AllowAnonymous();             // no authorization

// A delegate requirement can read the whole exchange, for example a route value.
app.MapGet("/documents/{owner}", DocumentAsync).RequireAuthorization(policy => policy.RequireAssertion(
    context => context.HttpContext.TryGetRouteValues(out RouteValueDictionary? values)
        && values!["owner"] is string owner
        && owner == context.User.Identity?.Name));
```

Register `UseAuthorization` after `UseRouting` (which publishes the endpoint) and after
`UseAuthentication` (which establishes `context.User`), and ahead of middleware that serves or caches
responses, such as `UseOutputCache`. A CORS middleware goes ahead of it.

A component that describes endpoints rather than serving them reads what the middleware will enforce:

```csharp
if (app.Context.TryGetAuthorizationOptions(out AuthorizationOptions? authorization))
{
    // null: the endpoint is open. Otherwise the policy's AuthenticationSchemes are the schemes it
    // authenticates and challenges through (none: the default authenticate scheme).
    AuthorizationPolicy? policy = authorization.GetEffectivePolicy(route.Metadata);
}
```

See `docs/DESIGN.md` for the evaluation flow, the combination rules, the fail-closed carrier design,
scheme selection, the fallback policy's reach, reading the options back, and the IdentityModel adapter
planned for #828.
