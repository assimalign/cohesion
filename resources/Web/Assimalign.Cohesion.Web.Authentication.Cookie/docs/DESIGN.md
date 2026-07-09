# Assimalign.Cohesion.Web.Authentication.Cookie &mdash; Design

## Design intent

The cookie authentication scheme: sign a user in by issuing a protected
ticket cookie, authenticate later requests by validating that cookie, and
drive the login / logout / access-denied flow. It is the
`IAuthenticationSignInHandler` implementation of the scheme model defined
in `Assimalign.Cohesion.Web.Authentication`.

The package is deliberately thin. It owns three things: the ticket's
binary shape, the sliding-expiration policy, and the redirect-vs-status
decision. Everything cryptographic is delegated; everything about scheme
dispatch belongs to the base package.

## Why the ticket is protected, never signed by hand

The cookie carries a `ClaimsPrincipal` plus its `AuthenticationProperties`
across requests. That payload must be **confidential and tamper-evident**:
a client must not read or forge it. The handler delegates this entirely to
`Assimalign.Cohesion.Security.DataProtection`'s `IDataProtector`
(AES-256-GCM keyed by an HKDF-derived subkey off a rotating key ring). The
handler never sees key bytes, never picks an algorithm, never rolls its
own MAC — it calls `Protect`/`Unprotect`.

This mirrors the precedent set when `Http.Antiforgery` was rewired onto a
pluggable protector seam (#774): key material and its lifecycle live in the
composition root, and the request-path component consumes a protector. The
protector is supplied on `CookieAuthenticationOptions.TicketProtector` by
`Web.Hosting`'s `AddCookie`, which derives it from the application key ring
scoped to a per-scheme purpose chain (`…Cookie` / scheme name / `v1`), so
two cookie schemes cannot read each other's tickets.

`Unprotect` throws `DataProtectionException` for a tampered, foreign, or
aged-out payload; the handler catches it and returns
`AuthenticateResult.Fail` rather than letting it escape — a bad cookie is
an expected, normal outcome, not an exception the pipeline should surface.

## Why a hand-rolled binary ticket serializer

`CookieTicketSerializer` writes the principal (identities, claims with
type/value/valueType/issuer, name- and role-claim types) and the
properties with `BinaryWriter`/`BinaryReader`. It uses no reflection and no
runtime-typed serializer, so it round-trips under NativeAOT and trimming —
the repo-wide constraint. A version byte guards future format changes. The
serialized bytes are only ever read back *after* `Unprotect` has verified
authenticity, so the reader is not parsing hostile input directly; it still
validates counts and the version defensively.

`DateTimeOffset` values are stored as UTC ticks (the offset is irrelevant
to the handler's expiry check), and a null string is distinguished from an
empty one with a sentinel so claims round-trip exactly.

## Expiration and sliding renewal

Two independent clocks govern a ticket:

- **Absolute expiry** — the ticket's `ExpiresUtc` (set on sign-in from
  `ExpireTimeSpan` unless the caller supplied its own). `AuthenticateAsync`
  rejects a ticket whose `ExpiresUtc` is at or before now.
- **Sliding renewal** — when `SlidingExpiration` is on and the request
  arrives past the *midpoint* of the ticket's lifetime, the handler
  re-issues the cookie with a fresh window of the same length. This is the
  ASP.NET Core rule; renewing only past the midpoint avoids writing a
  `Set-Cookie` on every request.

Renewal writes a new `Set-Cookie` during `AuthenticateAsync`. All cookie
emission funnels through one `IssueCookie` path that removes any
already-queued cookie of the same name first, so a renewal-then-sign-out in
one request still emits exactly one line for the name.

Time is taken from an injectable `TimeProvider` (`TimeProvider.System` by
default) so tests drive expiry and renewal deterministically.

## Redirect vs. status: keyed on endpoint metadata

`ChallengeAsync`/`ForbidAsync` must behave differently for a browser page
and a JSON API sharing one cookie scheme. The handler resolves the
`IApiEndpointMetadata` marker from the matched route's metadata (the #150
bag surfaced by `Web.Routing`'s `context.GetEndpointMetadata<T>()`,
reflection-free):

- **API endpoint** (marker present) &rarr; bare `401` (challenge) / `403`
  (forbid), no `Location`.
- **Otherwise** &rarr; `302` redirect to `LoginPath` (with an
  encoded `ReturnUrl`) / `AccessDeniedPath`.

This mirrors the .NET 10 cookie handler's `IApiEndpointMetadata`-keyed
decision. The return URL is the request path; the query string is omitted
in v1 (the redirect targets and the parameter name are configurable).

## Cookie hardening defaults

The emitted cookie defaults to `HttpOnly=true`, `SameSite=Lax`, `Path=/`.
`Secure` defaults to `false` because the transport scheme is not modeled as
a policy here; production deployments over HTTPS set `Secure=true`. A
persistent sign-in (`IsPersistent=true`) emits `Expires`/`Max-Age`; a
non-persistent one emits a session cookie. Sign-out emits a deletion cookie
(empty value, epoch `Expires`, zero `Max-Age`). The underlying
`HttpCookie` validates its value against the RFC 6265 cookie-octet grammar;
the protected ticket is base64url-encoded (unpadded), whose alphabet is a
subset of that grammar, so it can never split the `Set-Cookie` line.

## Interface-first posture

`CookieAuthenticationHandler` is `internal`; the public surface is
`IAuthenticationHandler`/`IAuthenticationSignInHandler` from the base
package plus the `CookieAuthentication.CreateHandler` factory the
composition root calls. `CookieAuthenticationOptions` and
`CookieAuthenticationDefaults` are the only other public types.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited. No reflection, no
runtime code generation: the serializer is hand-written, the protector is
BCL AEAD, base64url is `System.Buffers.Text.Base64Url`, and endpoint
metadata resolution is an `is`-test scan.

## Non-goals

- **OAuth2 / OIDC interactive login.** Redirect-based external sign-in is a
  follow-up; this handler only manages a first-party session cookie.
- **Cookie policy (consent, same-site overrides).** Cross-cutting cookie
  policy is `Web.CookiePolicy`'s concern.
- **Key management.** The rotating key ring and its persistence live in
  `Security.DataProtection`, composed at builder time in `Web.Hosting`.
