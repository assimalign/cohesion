# Cookie authentication

`Assimalign.Cohesion.Web.Authentication.Cookie` issues and validates protected
authentication-ticket cookies, renews sliding tickets, and handles sign-out
and browser login redirects. It implements the Web.Authentication scheme
contracts using Http.Cookies and Security.DataProtection; endpoint metadata
from Web.Routing selects redirects or bare API status codes.

Register the scheme through the authentication component integration:

```csharp
builder.Services.AddAuthentication(auth =>
{
    auth.Options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    auth.AddCookie(options =>
    {
        options.CookieName = "my-auth";
        options.LoginPath = "/account/login";
    });
});
app.UseAuthentication();
```

`CookieAuthenticationOptions.CookieName` is `HttpCookieName`. String assignments
use its implicit conversion and reject names outside the non-empty RFC 6265
`token` grammar with `ArgumentException` (`ParamName` `value`). `default` is
rejected by `AddCookie` and `CookieAuthenticationHandler.Create` with
`ArgumentException` (`ParamName` `options`) before any handler serves a request.
The message is `CookieAuthenticationOptions.CookieName must be a non-empty RFC
6265 token.` The registration path supplies the ticket protector; direct callers
must set `TicketProtector` themselves.

The name defaults to `.Cohesion.Cookies`. The ticket is essential, HttpOnly,
SameSite=Lax and scoped to `/`; effective HTTPS always adds Secure.

`LoginPath` and `AccessDeniedPath` are `HttpFieldValue` properties. Assign a
non-empty path with no control character or space; encode spaces as `%20`.
String assignments continue to work and invalid strings, empty values and
`default` fail immediately with `ArgumentException`. Read `.Value` when a string
is needed. Both properties were strings in 10.0.0-preview.1, so rebuild callers.

The property changed from `string` in 10.0.0-preview.1, requiring consumers to
rebuild. Invalid names now fail at assignment instead of sign-in or sign-out.
See [DESIGN.md](DESIGN.md) for validation, compatibility and ticket behavior.
