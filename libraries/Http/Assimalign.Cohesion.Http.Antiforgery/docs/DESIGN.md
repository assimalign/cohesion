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
that gap by cryptographically protecting both halves:

- **Cookie token** = `base64url(Protect(0x01 ‖ secret))`, where `secret` is 32
  random bytes. The protection lets the server detect an injected/forged
  cookie token.
- **Request token** = `base64url(Protect(0x02 ‖ nonce ‖ secret))`, where
  `nonce` is 16 random bytes and `secret` is the cookie token's secret. The
  protection binds the request token to one specific cookie secret.

The two payloads are **domain-separated** by a leading byte (`0x01` cookie,
`0x02` request) so a cookie token can never be replayed as a request token.
`HttpAntiforgeryTokenEngine` owns this framing; the authenticated protection of
each payload is delegated to a pluggable `IHttpAntiforgeryProtector` (see the
next section), so the engine holds no key material.

Validation recovers each payload through the protector (which fails closed on
tampering), checks the domain byte, and compares the recovered cookie secret in
fixed time via `CryptographicOperations.FixedTimeEquals`. A forged, truncated,
or wrong-key token is treated as a validation failure (return `false` / throw
`AntiforgeryValidationException`), never as an exception bubbling out of the
read path — `Base64Url` decode failures on untrusted input are caught and
mapped to "invalid".

## The protector seam: `IHttpAntiforgeryProtector`

Cryptography is a **pluggable seam**, not a hard-wired HMAC. `Protect` returns
an authenticated (and, for encrypting implementations, confidential) payload;
`TryUnprotect` fails closed for untrusted input (returns `false`, never
throws). The engine calls only these two operations.

- **Default** (`HmacHttpAntiforgeryProtector`): `Protect(p) = p ‖ HMAC-SHA256(key, p)`
  over `HttpAntiforgeryOptions.Key`, `TryUnprotect` recomputes and compares in
  fixed time. This preserves the original zero-dependency, single-process
  behavior — the cookie secret still travels in the clear inside the token,
  exactly as before.
- **Ring-backed** (recommended for multi-node / restart-stable): set
  `HttpAntiforgeryOptions.Protector` to an implementation backed by a
  persisted, rotating key ring. When set, `Protector` **supersedes `Key`
  entirely**. The Cohesion data-protection provider
  (`Assimalign.Cohesion.Security.DataProtection`) is the intended backing: its
  versioned key-id header makes rotation transparent, and pointing every node at
  a shared key repository means nodes validate each other's tokens **without
  hand-distributing raw key bytes**.

This package takes **no dependency** on any data-protection library. The
adapter from `IDataProtector` to `IHttpAntiforgeryProtector` is a few lines and
lives in the composition root (a `*.Hosting` project), keeping this package
lean and free of key-management concerns.

## Why an application key, and the default

Signing requires a key. `HttpAntiforgeryOptions.Key` defaults to a fresh
32-byte random value per `HttpAntiforgeryOptions` instance. That default is
correct and zero-config for a single process, but it has two consequences
that are documented on the type:

- Tokens minted before a process restart will not validate afterward.
- Multiple instances behind a load balancer reject each other's tokens.

Cross-process / restart-stable deployments should set a ring-backed
`Protector` (which supersedes `Key`) rather than hand-distributing a shared
static `Key`. We chose a secure-by-default-but-explicit-for-scale posture over
silently persisting a key somewhere, because key storage is a deployment
decision the library should not make — the seam lets the host make it once, in
the data-protection layer, for antiforgery and every other protected artifact.

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

- **Key management / rotation.** The package protects payloads through the
  `IHttpAntiforgeryProtector` seam; it does not persist, rotate, or distribute
  keys itself. Ring-backed persistence and rotation are available by plugging a
  data-protection-backed protector in at the host — that concern lives in the
  key ring, not here.
- **Identity binding.** ASP.NET Core's antiforgery can embed the
  authenticated username/claims in the token. This package binds the request
  token to the cookie token only. Identity binding can layer on later without
  changing the wire shape.
- **Triggering form parsing.** Reading the request token from a form relies
  on the Forms layer having parsed the body; antiforgery does not own that.
- **Middleware.** This package provides the service, feature, and ergonomics.
  A pipeline that auto-validates every unsafe request is a higher-layer
  concern.
