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
this package's grafted `AddCookie` verb (an `extension(AuthenticationBuilder)`
member — moved here from `Web.Hosting` under the Web-area dependency rule),
which derives it from the builder's key ring scoped to a per-scheme purpose
chain (`…Cookie` / scheme name / `v1`), so two cookie schemes cannot read
each other's tickets.

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

The emitted cookie defaults to `HttpOnly=true`, `SameSite=Lax`, `Path=/`. A
persistent sign-in (`IsPersistent=true`) emits `Expires`/`Max-Age`; a
non-persistent one emits a session cookie. Sign-out emits a deletion cookie
(empty value, epoch `Expires`, zero `Max-Age`). The underlying
`HttpCookie` validates its value against the RFC 6265 cookie-octet grammar;
the protected ticket is base64url-encoded (unpadded), whose alphabet is a
subset of that grammar, so it can never split the `Set-Cookie` line.

### `Secure` — a floor on the effective scheme, not a policy surface

Every cookie the handler emits (issue, sliding renewal, sign-out deletion) is
built from the `Cookie` template and then given one floor: **when the effective
request scheme is HTTPS, `Secure` is set**, whatever the template says. The
template's own `Secure = true` still marks the cookie `Secure` on every request.

"Effective" is `context.EffectiveScheme` from `Assimalign.Cohesion.Http.Forwarded`
(owner decision 3 in `docs/programs/HTTP_WEB_PROGRAM_PLAN.md` §7.4: consumers read
the effective values; nothing rewrites the request). A direct TLS connection
counts, and so does TLS terminated at a proxy that `UseForwardedHeaders` trusts.
Without the forwarded-headers middleware — or from a peer outside its trust
model — the effective scheme is the transport-derived one, so a client that
sends `X-Forwarded-Proto: https` itself changes nothing. Register
`UseForwardedHeaders` ahead of `UseAuthentication`.

Why a floor and not the options considered with it (#1050, defect D7):

- **Keep the static template (the previous behavior).** `Secure` defaulted to
  `false` and only the template decided. A deployment that served HTTPS and
  forgot `Secure = true` issued the ticket without it, and the browser would
  send the ticket over any later plaintext request to the host. Behind a
  TLS-terminating proxy there was no way to say "Secure when the client used
  HTTPS" at all. Rejected.
- **A tri-state secure policy option** (ASP.NET's `CookieSecurePolicy`:
  `SameAsRequest` / `Always` / `None`). That is new public surface and a
  default-choosing decision, and cookie-policy enforcement — including the
  secure default for this handler — is #156's scope (`Web.CookiePolicy`). Not
  taken here.
- **Chosen: template OR effective HTTPS.** It is a pure hardening with no public
  API change: over HTTPS the cookie can only gain `Secure`, and over plaintext
  the template decides exactly as before. What it removes is the ability to emit
  a non-`Secure` ticket over HTTPS — a credential the browser would then also
  send over plaintext, since only the `Secure` attribute confines a cookie to
  secure channels (RFC 6265bis §4.1.2.5). It composes with #156: whatever policy
  that item adds reads the same effective scheme.

### Redirects and the return URL need no host or scheme

The challenge and forbid redirects emit a relative `Location` — the configured
path plus `ReturnUrl=<request path>` — which the user agent resolves against the
URL it actually requested. Nothing in the handler reads `Request.Host`,
`Request.Scheme`, or the connection for them, so they are correct behind a proxy
by construction; an absolute return URL would have to be built from the
effective scheme and host.

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
  `Security.DataProtection`, carried at builder time by
  `AuthenticationBuilder.DataProtectionProvider` (the default key ring, or
  a provider the application passes to `AddAuthentication`).
