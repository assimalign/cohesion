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
- `CookieAuthenticationHandler.Create(options)` &mdash; the factory the
  composition root calls; it returns the public sealed handler with private construction.

## How it fits

- Implements `IAuthenticationSignInHandler` from
  `Assimalign.Cohesion.Web.Authentication` (the scheme model).
- Seals tickets through `Assimalign.Cohesion.Security.DataProtection`
  &mdash; keys are never hand-rolled.
- Keys its redirect-vs-`401`/`403` decision on the sealed `ApiEndpointMetadata`
  marker resolved through `Assimalign.Cohesion.Web.Routing`; attach `ApiEndpointMetadata.Instance`
  to an API endpoint.

Register it at the composition root, not here:

```csharp
builder.Services.AddAuthentication(auth =>
{
    auth.Options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    auth.AddCookie(o =>
    {
        o.LoginPath = "/account/login";
        o.Cookie.Secure = true;
    });
});

app.UseAuthentication();
```

Over HTTPS the ticket cookie is always `Secure`, even without `Cookie.Secure`:
the handler reads the effective scheme, so TLS terminated at a trusted proxy
counts once `UseForwardedHeaders` runs ahead of `UseAuthentication`.

The ticket cookie is essential by default (`Cookie.IsEssential`), so a
`UseCookiePolicy` consent requirement never stops a user from signing in.

See [docs/DESIGN.md](docs/DESIGN.md) for the ticket format, sliding-renewal
rule, and the redirect-vs-status decision.
