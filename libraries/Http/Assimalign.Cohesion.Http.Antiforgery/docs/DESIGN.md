# Assimalign.Cohesion.Http.Antiforgery — Design

Server-side cross-site request forgery (CSRF) protection for the Cohesion
HTTP family. This document captures the design intent so future readers do
not have to re-derive it from the code.

## Design intent

Restore the familiar antiforgery ergonomics (`GetAndStoreTokens`,
`ValidateRequestAsync`) on top of the Cohesion HTTP protocol core, which
deliberately knows nothing about CSRF. The package realizes the
`IHttpAntiforgery` / `IHttpAntiforgeryFeature` abstractions and the
`HttpAntiforgeryTokenSet` model using only BCL cryptography — no
`Microsoft.Extensions.*`, no external data-protection stack.

## Token model: signed double-submit

The classic double-submit cookie defense puts a random value in a cookie and
requires the same value to be echoed in a header or form field. Its weakness
is integrity: an attacker who can set a cookie (subdomain takeover, "cookie
tossing") can also supply the matching request value. This package closes
that gap by signing both halves with an application HMAC key:

- **Cookie token** = `base64url(secret ‖ HMAC(key, 0x01 ‖ secret))`, where
  `secret` is 32 random bytes. The signature lets the server detect an
  injected/forged cookie token.
- **Request token** = `base64url(nonce ‖ HMAC(key, 0x02 ‖ nonce ‖ secret))`,
  where `nonce` is 16 random bytes and `secret` is the cookie token's secret.
  The HMAC binds the request token to one specific cookie token and cannot be
  produced without the key.

The two HMAC uses are **domain-separated** by a leading byte (`0x01` cookie,
`0x02` request) so a cookie token can never be replayed as a request token.

Validation recomputes each HMAC and compares with
`CryptographicOperations.FixedTimeEquals` to avoid timing side channels. A
forged, truncated, or wrong-key token is treated as a validation failure
(return `false` / throw `AntiforgeryValidationException`), never as an
exception bubbling out of the read path — `Base64Url` decode failures on
untrusted input are caught and mapped to "invalid".

## Why an application key, and the default

Signing requires a key. `HttpAntiforgeryOptions.Key` defaults to a fresh
32-byte random value per `HttpAntiforgeryOptions` instance. That default is
correct and zero-config for a single process, but it has two consequences
that are documented on the type:

- Tokens minted before a process restart will not validate afterward.
- Multiple instances behind a load balancer reject each other's tokens.

Cross-process / restart-stable deployments **must** set a shared `Key`. We
chose a secure-by-default-but-explicit-for-scale posture over silently
persisting a key somewhere, because key storage is a deployment decision the
library should not make.

## Service shape: stateless singleton over a passed-in context

`IHttpAntiforgery` methods each take the `IHttpContext` they operate on, so
the implementation (`HttpAntiforgeryService`) is stateless aside from its
options + engine and is safe to share across requests. `HttpAntiforgery.Create`
is the public factory; the internal service stays hidden behind the
interface (interface-first). The `IHttpAntiforgeryFeature` carries the shared
service on a context so downstream handlers resolve the *same* configured
service — and therefore the same signing key — via
`context.Antiforgery` / `context.RequireAntiforgery`.

## Dependency direction

```
Assimalign.Cohesion.Http              (protocol core: request/response/headers)
        ▲
        │
Assimalign.Cohesion.Http.Cookies      (cookie-token storage: request/response cookies)
Assimalign.Cohesion.Http.Forms        (form-field request-token extraction)
        ▲
        │
Assimalign.Cohesion.Http.Antiforgery  (this package)
```

The package reads the cookie token from `request.Cookies` and writes it to
`response.Cookies` (Cookies package). It reads the request token from
`request.Form[field]` (Forms package) and falls back to
`request.Headers[header]`. It does not re-implement cookie parsing or form
parsing.

A note on the form path: `request.Form` returns the *already-parsed* form.
Antiforgery does not itself trigger body parsing — that is the Forms layer's
job (a forms middleware, or an explicit `ReadFormAsync`). When the form has
not been parsed, the form-token path yields nothing and the header path is
used. The header path is therefore always available; the form path requires
the body to have been parsed first.

## Safe-method skip

`IsRequestValidAsync` returns `true` for GET, HEAD, OPTIONS, and TRACE
without inspecting tokens, per RFC 9110 §9.2.1 (safe methods are not
state-changing). Only unsafe methods require a valid pair.

## Response side effects

`GetAndStoreTokens` / `SetCookieTokenAndHeader` set the cookie token through
the response cookie feature and apply `Cache-Control: no-cache, no-store`,
`Pragma: no-cache`, and `X-Frame-Options: SAMEORIGIN`. A page carrying a
token must not be cached or a shared cache could serve one user's token to
another; `X-Frame-Options` reduces clickjacking that could drive token
submission.

## AOT posture

No reflection, no runtime code generation, no dynamic serialization. All
crypto is BCL (`HMACSHA256.HashData`, `RandomNumberGenerator`,
`CryptographicOperations.FixedTimeEquals`, `Base64Url`). Token payloads are
small and use stack buffers, so the hot paths are allocation-light.

## Non-goals

- **Key management / rotation.** The package consumes a key; it does not
  persist, rotate, or distribute one. That belongs to the host.
- **Identity binding.** ASP.NET Core's antiforgery can embed the
  authenticated username/claims in the token. This package binds the request
  token to the cookie token only. Identity binding can layer on later without
  changing the wire shape.
- **Triggering form parsing.** Reading the request token from a form relies
  on the Forms layer having parsed the body; antiforgery does not own that.
- **Middleware.** This package provides the service, feature, and ergonomics.
  A pipeline that auto-validates every unsafe request is a higher-layer
  concern.
