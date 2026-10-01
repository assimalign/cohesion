# Assimalign.Cohesion.Http.Antiforgery

Server-side cross-site request forgery (CSRF) protection for the Cohesion
HTTP family. Implements `IHttpAntiforgery` with a **signed double-submit**
token model built on BCL cryptography (HMAC-SHA256) — no
`Microsoft.Extensions.*` and no external data-protection dependency.

## Why a separate package

The HTTP protocol core (`Assimalign.Cohesion.Http`) deliberately knows
nothing about CSRF. Antiforgery is a server-side application concern that
needs cookies and form parsing, so it layers on top of the protocol core via
`Assimalign.Cohesion.Http.Cookies` (cookie-token storage),
`Assimalign.Cohesion.Http.Forms` (form-field token extraction) and
`Assimalign.Cohesion.Http.Forwarded` (the effective scheme that decides whether
the cookie token is `Secure`). Protocol-only
consumers (clients, proxies, edge caches) should not reference this package.

## Surface

| Type | Role |
| --- | --- |
| `IHttpAntiforgery` | The antiforgery service contract. |
| `HttpAntiforgery` | Static factory: `HttpAntiforgery.Create(...)`. |
| `HttpAntiforgeryOptions` | Cookie/form/header names, cookie attributes, HMAC key. |
| `HttpAntiforgeryTokenSet` | The cookie + request token pair returned to callers. |
| `IHttpAntiforgeryFeature` | Carries the service on an `IHttpContext`. |
| `AntiforgeryValidationException` | Thrown by `ValidateRequestAsync` on failure. |
| `context.Antiforgery` / `context.RequireAntiforgery` | Ergonomic context access. |

## Usage

In a Web application, use `Assimalign.Cohesion.Web.Antiforgery` instead of
wiring this package by hand: `AddAntiforgery` registers the service for every
exchange (sealing tokens with a purpose-bound data-protection protector when
given a provider), and `UseAntiforgery` validates protected endpoints.

Outside the Web pipeline, create one service per application (it is stateless
and shareable). The default protector signs with a per-process random key,
which is for development only: tokens die on restart and instances reject each
other's tokens. Deployed applications set `Protector` to a persisted, rotating
key ring.

```csharp
IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options =>
{
    options.Protector = ringBackedProtector; // omit only in development
    // The cookie token is Secure on every HTTPS request; CookieSecure = true
    // forces it on plaintext requests too.
});

// Render path: mint + store the cookie token, hand the request token to the view.
HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
// tokens.RequestToken  -> put in a hidden form field (tokens.FormFieldName) or send to the SPA
// tokens.CookieToken   -> already queued as a Set-Cookie on the response

// Submit path: validate the unsafe request.
await antiforgery.ValidateRequestAsync(context); // throws AntiforgeryValidationException on failure
// or, non-throwing:
bool ok = await antiforgery.IsRequestValidAsync(context);
```

The request token is read from the configured form field
(`__RequestVerificationToken` by default, requires the body to have been
parsed by the Forms layer) or the configured header (`X-CSRF-TOKEN` by
default, always available). Safe methods (GET, HEAD, OPTIONS, TRACE) skip
validation.

## Token model

- Cookie token: `base64url(Protect(0x01 ‖ secret))`
- Request token: `base64url(Protect(0x02 ‖ nonce ‖ secret))`

`Protect` is the configured `IHttpAntiforgeryProtector`: by default
`payload ‖ HMAC-SHA256(key, payload)`. The request token is cryptographically
bound to its cookie token and protected by the same protector, so it cannot be
forged or replayed across cookie tokens. See `docs/DESIGN.md` for the full
rationale, the key-management posture, and non-goals.

## Standards

Follows OWASP CSRF prevention guidance (signed double-submit cookie,
constant-time comparison). Safe-method classification follows RFC 9110
§9.2.1.
