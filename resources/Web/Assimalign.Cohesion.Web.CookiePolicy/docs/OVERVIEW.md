# Assimalign.Cohesion.Web.CookiePolicy — Overview

Cookie-policy enforcement for the Cohesion Web pipeline. `UseCookiePolicy` makes every cookie the
application appends through `context.Response.Cookies` pass the site's policy before it reaches a
`Set-Cookie` field. The policy covers consent for non-essential cookies, the `Secure`, `HttpOnly`,
and minimum `SameSite` floors, the RFC 6265bis requirements a browser enforces by ignoring a cookie,
and the 400-day lifetime cap. The cookie model it enforces against lives in
`Assimalign.Cohesion.Http.Cookies`; this package owns the policy.

## What it provides

- **`UseCookiePolicy(Action<CookiePolicyOptions>?)`** — a pipeline verb on
  `IWebApplicationPipelineBuilder`. It validates and captures the options at registration and
  replaces each exchange's response cookie feature, so `Web.Sessions`, `Web.Authentication.Cookie`,
  `Http.Antiforgery`, and application code are all covered without changes of their own.
- **`CookiePolicyOptions`**:
  - `Secure`: `SameAsRequest` by default, which follows the effective scheme, so a trusted
    TLS-terminating proxy counts; or `Always`, or `None`.
  - `HttpOnly`: `None` or `Always`.
  - `MinimumSameSitePolicy`.
  - `SameSiteNoneWithoutSecure` and `PrefixViolation`: `Upgrade` or `Reject`.
  - `MaxLifetime` and `TimeProvider`: the lifetime cap and the clock it is applied against.
  - `CheckConsentNeeded`, plus the consent cookie's name, value, and attributes.
  - `OnRejected`: a hook that observes every dropped cookie.
- **`ICookieConsentFeature`** — per-exchange consent state (`IsConsentNeeded`, `HasConsent`,
  `CanTrack`) and the `GrantConsent` / `WithdrawConsent` verbs. Resolve it with
  `context.Features.Get<ICookieConsentFeature>()`.
- **`CookiePolicyRejectionContext`** / **`CookiePolicyRejectionReason`** — what `OnRejected` receives:
  the exchange, the cookie as appended, and why it was dropped.

The defaults are secure without breaking ordinary sites: `Secure` whenever the client used HTTPS,
RFC 6265bis violations repaired rather than dropped, the 400-day cap, no `HttpOnly` or `SameSite`
floor, and no consent requirement. A rule only ever adds protection; nothing the application set is
removed.

## Usage

```csharp
using Assimalign.Cohesion.Web.CookiePolicy;
using Assimalign.Cohesion.Web.ForwardedHeaders;

app.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);

// Early: before anything that writes cookies.
app.UseCookiePolicy(options =>
{
    options.MinimumSameSitePolicy = HttpCookieSameSiteMode.Lax;
    options.CheckConsentNeeded = context => IsInConsentRegion(context);
    options.OnRejected = rejection => log.CookieDropped(rejection.Cookie.Name, rejection.Reason);
});

app.UseSessions();
app.UseAuthentication();

// A consent endpoint.
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value == "/consent")
    {
        context.Features.Get<ICookieConsentFeature>()?.GrantConsent();
        context.Response.StatusCode = HttpStatusCode.NoContent;
        return;
    }

    await next(context);
});

// Application cookies are judged as they are appended. Mark the ones the site cannot work without.
context.Response.Cookies.Add(new HttpCookie("cart", id, new HttpCookieOptions { IsEssential = true }));
context.Response.Cookies.Add(new HttpCookie("analytics", visitor)); // dropped until consent is given
```

The Cookie authentication ticket is essential by default, so a consent requirement never prevents
sign-in.

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline abstractions the verb extends.
- `Assimalign.Cohesion.Http.Cookies` — the cookie model: `HttpCookie`, the response cookie feature
  the policy replaces, and `HttpCookie.ClampLifetime`.
- `Assimalign.Cohesion.Http.Forwarded` — the `EffectiveScheme` read behind `SameAsRequest`, which falls
  back to the transport's scheme when no forwarded-headers middleware ran.
- `Assimalign.Cohesion.Http` — the HTTP context and feature collection.

No DI, configuration, logging, or hosting dependency. The package is delivered to applications
through the `App.Web` shared framework (via `Sdk.Web`). See [DESIGN.md](DESIGN.md) for the
interception mechanism, rule order, defaults, consent model, pipeline placement, and non-goals.
