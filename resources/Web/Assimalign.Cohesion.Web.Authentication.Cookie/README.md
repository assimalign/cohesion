# Assimalign.Cohesion.Web.Authentication.Cookie

The cookie authentication scheme handler for the Cohesion web stack. Signs
users in with a data-protected ticket cookie, validates that cookie on
later requests, applies sliding expiration, and drives the
login / logout / access-denied flow.

## What it provides

- `CookieAuthenticationOptions` &mdash; cookie name and attributes, login /
  logout / access-denied paths, ticket lifetime, sliding-expiration toggle,
  and the `IDataProtector` that seals the ticket.
- `CookieAuthenticationDefaults` &mdash; the default scheme name
  (`"Cookies"`) and paths.
- `CookieAuthentication.CreateHandler(options)` &mdash; the factory the
  composition root calls; the concrete handler stays internal.

## How it fits

- Implements `IAuthenticationSignInHandler` from
  `Assimalign.Cohesion.Web.Authentication` (the scheme model).
- Seals tickets through `Assimalign.Cohesion.Security.DataProtection`
  &mdash; keys are never hand-rolled.
- Keys its redirect-vs-`401`/`403` decision on the `IApiEndpointMetadata`
  endpoint marker resolved through `Assimalign.Cohesion.Web.Routing`.

Register it at the composition root, not here:

```csharp
builder.AddAuthentication(o => o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
       .AddCookie(o =>
       {
           o.LoginPath = "/account/login";
           o.Cookie.Secure = true;
       });

app.UseAuthentication();
```

See [docs/DESIGN.md](docs/DESIGN.md) for the ticket format, sliding-renewal
rule, and the redirect-vs-status decision.
