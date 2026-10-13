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
sealed `ApiEndpointMetadata` marker from the matched route's metadata (the #150
bag surfaced by `Web.Routing`'s `context.GetEndpointMetadata<T>()`,
reflection-free):

- **API endpoint** (marker present) &rarr; bare `401` (challenge) / `403`
  (forbid), no `Location`.
- **Otherwise** &rarr; `302` redirect to `LoginPath` (with an
  encoded `ReturnUrl`) / `AccessDeniedPath`.

Endpoints attach `ApiEndpointMetadata.Instance`. The cookie handler reads that concrete
marker directly; no empty interface or custom marker implementation is needed.
The return URL is the request path; the query string is omitted
in v1 (the redirect targets and the parameter name are configurable). The
path is written percent-encoded (`HttpPath.ToUriComponent`, #1324's review):
the transports hand it over decoded, and the login page redirects to the
return URL, so `/caf%C3%A9` carried as `/café` ended in a `Location` the
HTTP/1.1 writer sends as `/caf?`, and `/100%25` as `/100%`, which is not a URI
reference (RFC 9110 §10.2.2).

## Cookie hardening defaults

The emitted cookie defaults to `HttpOnly=true`, `SameSite=Lax`, `Path=/`, and
`IsEssential=true` (see "Under the cookie policy" below). A
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
  `SameAsRequest` / `Always` / `None`) on this handler. That is new public
  surface and a default-choosing decision for every cookie, not just the
  ticket, so it lives in `Web.CookiePolicy` (#156) as `CookiePolicyOptions.Secure`
  rather than here.
- **Chosen: template OR effective HTTPS.** It is a pure hardening with no public
  API change: over HTTPS the cookie can only gain `Secure`, and over plaintext
  the template decides exactly as before. What it removes is the ability to emit
  a non-`Secure` ticket over HTTPS — a credential the browser would then also
  send over plaintext, since only the `Secure` attribute confines a cookie to
  secure channels (RFC 6265bis §4.1.2.5). It composes with `Web.CookiePolicy`
  (#156), whose default `SameAsRequest` reads the same effective scheme, so
  the two never disagree.

### Under the cookie policy (#156)

The handler emits through `response.Cookies`, so when `UseCookiePolicy` runs the
policy judges the ticket, its renewal, and the sign-out deletion like any other
cookie. Its consent rule, attribute floors, and RFC 6265bis requirements apply.
What that means for the defaults:

- **The ticket is essential.** `Cookie.IsEssential` defaults to `true`. That was
  the one gap #156 found: before the policy existed nothing read `IsEssential`,
  so the template never set it, and under a consent requirement a non-essential
  ticket would be dropped on every request; a user could never stay signed in.
  Signing in is something the user asked for, which is what "essential" means.
  An application that collects consent before it offers sign-in can set it to
  `false`.
- **`Secure` behind a proxy** is the handler's floor above plus the policy's
  `SameAsRequest`; both read the effective scheme.
- **`SameSite`** stays `Lax` unless `MinimumSameSitePolicy` raises it. `Strict`
  withholds the ticket from top-level navigations that arrive from another
  site, so a user following a link in is treated as signed out on that first
  request.
- **A `__Host-` ticket name** works: the template's `Path=/` and absent `Domain`,
  plus the `Secure` floor over HTTPS, satisfy the prefix. Over plaintext the
  policy's `Upgrade` adds `Secure`, which only a `localhost` client accepts, and
  `Reject` drops the ticket.
- **Sign-out always passes:** the deletion cookie is a deletion, which consent
  never blocks.

The handler takes no reference to `Web.CookiePolicy`; the two meet only in the
cookie model.

### Redirects and the return URL need no host or scheme

The challenge and forbid redirects emit a relative `Location` — the configured
path plus `ReturnUrl=<request path>` — which the user agent resolves against the
URL it actually requested. Nothing in the handler reads `Request.Host`,
`Request.Scheme`, or the connection for them, so they are correct behind a proxy
by construction; an absolute return URL would have to be built from the
effective scheme and host.

## Public implementation and preview.1 compatibility (#1465)

Owner decision 59 makes `CookieAuthenticationHandler` public and sealed, with a private
constructor and `Create(options)` returning the concrete handler.
`IAuthenticationHandler`/`IAuthenticationSignInHandler` remain the extension contracts.
Creation keeps the same options and validation.

10.0.0-preview.1 shipped `CookieAuthentication.CreateHandler(options)`. The static
`CookieAuthentication` class is removed: migrate to
`CookieAuthenticationHandler.Create(options)` and rebuild. This is source- and
binary-breaking, accepted during previews on decision 15's terms. Authentication,
ticket protection and redirect behavior are unchanged by the conversion.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited. No reflection, no
runtime code generation: the serializer is hand-written, the protector is
BCL AEAD, base64url is `System.Buffers.Text.Base64Url`, and endpoint
metadata resolution is an `is`-test scan.

## Non-goals

- **OAuth2 / OIDC interactive login.** Redirect-based external sign-in is a
  follow-up; this handler only manages a first-party session cookie.
- **Cookie policy (consent, same-site overrides).** Cross-cutting cookie
  policy is `Web.CookiePolicy`'s concern; the handler only marks its ticket
  essential (see "Under the cookie policy").
- **Key management.** The rotating key ring and its persistence live in
  `Security.DataProtection`, carried at builder time by
  `AuthenticationBuilder.DataProtectionProvider` (the default key ring, or
  a provider the application passes to `AuthenticationBuilder.UseDataProtection`
  inside `builder.Services.AddAuthentication`).

## Diagnostics

The internal `CookieAuthenticationEventSource` provider is named
`Assimalign.Cohesion.Web.Authentication.Cookie`. A `Web.Server` reference
supplies request correlation; the root authentication dispatcher emits nothing.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | TicketRejected | Verbose | scheme, reason, exceptionType, exceptionMessage, innerExceptionType, requestId |

Reasons are NotBase64Url, UnprotectFailed, PayloadMalformed and Expired. Exception
types identify parsing or unprotect failures; exception messages apply only to
UnprotectFailed. The message is allowlisted to the six fixed
built-in DataProtection failure strings; arbitrary custom-protector text is omitted.
An inner exception type appears only for the fixed key-repository-read failure, never
for a cryptographic verification failure. Inner exception messages are never written.

The lazily published PollingCounter `rejected-tickets` counts every rejection, including
with no listener, once per request and scheme. Handler/result caching prevents counting
Authorization re-authentication or a subsequent challenge twice. Missing cookies and
successful authentication do not count. The counter is maintained independently of delivery.

Every allocating payload and request-id read is inside the Verbose enable check. Correlation
uses `IWebRequestIdFeature`, never `Activity.Current`. No cookie value, plaintext, principal,
claim, return URL or request path is written. This is additive diagnostics with no public
API or response behavior change from 10.0.0-preview.1.

## API endpoint marker compatibility (#1466)

In 10.0.0-preview.1 the handler looked up `IApiEndpointMetadata`. Owner decision 59 removes
that empty interface from Web.Authentication and changes this handler to look up the sealed
`ApiEndpointMetadata` marker. Existing endpoints attaching `ApiEndpointMetadata.Instance`
retain bare 401/403 responses without a Location header or response body; unmarked endpoints
retain their login/access-denied redirects. Tests pin both paths.

Removing the preview.1 interface is source- and binary-breaking on decision 15's terms.
Applications with a custom interface implementation must attach `ApiEndpointMetadata.Instance`
instead and rebuild; an unrelated custom metadata object no longer suppresses a redirect.

## Exception messages (#1467)

Exception text lives in `src/Properties/ErrorMessages.resx`. The shared build targets generate
an internal `ErrorMessages` accessor in this project's root namespace under the intermediate
output directory; no designer is checked in. Formatted messages use
`string.Format(CultureInfo.CurrentCulture, ErrorMessages.<Name>, ...)`. Resource extraction
preserves the existing exception text and input-disclosure rules, including any diagnostic
type-only exclusions. EventSource templates, log messages and response bodies retain their
existing ownership. This is a conventions change with no public API or behavior break against
10.0.0-preview.1.

The expected rejection or cancellation messages are cached in static readonly fields: `AuthenticationCookieEncodingInvalid`, `AuthenticationCookiePayloadMalformed`, `AuthenticationCookieUnprotectFailed`, `AuthenticationTicketExpired`. Their ordinary lifecycle paths perform no resource lookup per exception.
