# Assimalign.Cohesion.Web.Sessions — Design

Per-request session support for the Cohesion Web pipeline. This document records the reasoning behind
the middleware's shape so a future reader does not re-derive it from the code.

## Design intent

`Assimalign.Cohesion.Http.Sessions` owns the session *surface* (`IHttpSession`, typed accessors) and
the async *store seam* (`IHttpSessionStore`, the in-memory default, the AOT-safe frame serializer). It
deliberately ships no cookie and no per-request middleware — those are Web-layer concerns. This package
supplies exactly that missing layer: it resolves a session per request, carries the session id in a
hardened cookie, and drives the store's `Load`/`Commit` lifecycle around the pipeline. The split keeps
the HTTP session model usable without the Web runtime, and keeps this package a thin, dependency-free
feature library composed against the Web root.

Store-backed sessions are created through `IHttpSessionStore.CreateSession` and
held through the public `IHttpStoredSession` contract. Identifier regeneration
uses that contract's `ReassignId` operation after removing the old store entry.
The HTTP package owns buffering and framing; this package owns cookies and the
request lifecycle. No shipped-library friend access is required.

Managed identifier regeneration is available only for the session created by this
feature. Replacing `context.Session` disables managed regeneration even when the
replacement implements `IHttpStoredSession`: its backing store may differ from
the middleware's store. Assigning the same managed instance preserves ownership.

Out-of-process session state is the motivating goal: Cohesion is a multi-service framework with a load
balancer, so multi-instance web serving needs sticky-session-free affinity. Because the middleware runs
entirely against the `IHttpSessionStore` seam, swapping the in-memory default for a distributed adapter
is a one-argument change to `UseSessions` — nothing else in the pipeline moves.

## Lifecycle — lazy, with the cookie established up front

The middleware installs a lazy session feature (`WebSessionFeature`) before the pipeline runs and
commits after it unwinds. The feature does nothing until the application first touches the session:

1. **First access** (`context.LoadSessionAsync()` or `context.Session`) resolves the session id — from
   the request cookie, or a freshly minted cryptographically random id. For a *new* id the session-id
   cookie is appended **synchronously, at that moment**. The store-backed session is then created (and,
   via `LoadSessionAsync`, loaded).
2. **During the request** the application reads/writes the session dictionary.
3. **After `next`** the middleware commits *only if the session was accessed*: a modified session is
   serialized and written to the store; an accessed-but-unmodified session slides the store's idle
   window instead. An untouched session performs no I/O and mints no cookie.

### Why the cookie is established at first access, not post-`next`

`Set-Cookie` must be on the response before the head commits, and the head can commit mid-pipeline (a
streamed response). Appending the cookie **at id-mint time** — synchronously, on first access, before
the application writes the body — guarantees it precedes the head. The alternative (queueing the cookie
after `next`) races a response that already started. Consequently the post-`next` commit path touches
the *store only*, never headers, so it is safe even after the head has committed.

The one case the establishment path cannot serve is a session first touched *after* the head already
started: a committed head can carry no new field, so cookie establishment is skipped (best-effort) and
the guard is the `IHttpResponseStreamingFeature.HasStarted` / `Headers.IsReadOnly` pattern shared with
the rest of the Web stack. The session still functions in memory for the remainder of that request —
but it is **not committed to the store**: the client can never present the undelivered id again, so
persisting it would only accumulate orphaned entries until the idle timeout reaped them. (A session the
request *presented by cookie* commits normally after a started head — the commit path touches only the
store.)

### Why a request that already carries a cookie is not re-issued

When the request presents a valid session cookie the client already holds the id, so no `Set-Cookie` is
emitted; the server slides the store's idle window instead. This matches conventional session behavior
and avoids a redundant header on every request. Sliding expiration lives server-side (the store's idle
timeout), so a session-scoped cookie needs no periodic re-issue.

## Concurrency contract — last-commit-wins (echoed from the store seam)

The store contract this package composes is **last-commit-wins**: `CommitAsync` writes the whole payload
back unconditionally, with no read-modify-write lock and no per-key merge. Two concurrent requests for
the same session each load a snapshot and the later commit replaces the earlier wholesale. This is the
semantics every `IHttpSessionStore` — including future distributed adapters — must implement, chosen
because it is what distributed stores honor cheaply. The full contract (and the rejected compare-and-swap
alternative) is documented on `IHttpSessionStore` and in the `Http.Sessions` `DESIGN.md`.

## Session-id generation and regeneration

Ids are 128 bits of `RandomNumberGenerator` entropy, base64url-encoded (URL-safe, unpadded, cookie-octet
safe). `context.RegenerateSessionIdAsync()` is the session-fixation defense: it mints a new id, removes
the old id from the store, re-keys the buffered state to the new id (so the next commit writes under it),
and replaces the session-id cookie. Regeneration is only valid while the response head has not started
(the cookie must still be replaceable) — it throws otherwise — and requires a session to have been
established on the request. Call it immediately after a privilege change such as authentication.

## Cookie posture

The session-id cookie is built through the hardened `Http.Cookies` model with secure defaults:

- **`HttpOnly`** — from `HttpSessionOptions.CookieHttpOnly` (default `true`).
- **`SameSite=Lax`** — a fixed secure default (CSRF-resistant while surviving top-level navigation).
- **`Secure`** — set when the *effective* scheme is HTTPS (see "Behind a proxy" below), so the cookie
  round-trips over the plaintext in-memory test transport yet is Secure in production HTTPS, including
  behind a TLS-terminating proxy.
- **Session-scoped** — no `Max-Age`/`Expires`; the cookie clears when the browser session ends, and
  server-side idle timeout governs true expiry.
- **Name / Path** — from `HttpSessionOptions.CookieName` / `CookiePath`. `UseSessions` checks both
  before registering middleware: the name through the `HttpCookie` constructor against the RFC 6265
  §4.1.1 non-empty `token` grammar (#1416), and the path through the `HttpCookieOptions.Path` setter
  against `path-value` (#1153). A name such as `a b` or a path such as `/tenant;x` or `/café` fails
  composition. Token punctuation remains valid in names; whitespace, separators, control characters,
  non-ASCII characters, null and empty names do not. Reusing the cookie model keeps composition and
  request-time emission on the same grammar.
- **Not essential by default** — `HttpSessionOptions.CookieIsEssential` (default `false`) marks the
  cookie for a cookie-consent policy (Web.CookiePolicy). Under a consent requirement a non-essential
  session cookie is dropped until the user consents, because a session usually holds state that needs
  consent; an application that cannot work without its session (a cart, a multi-step form) sets it to
  `true`.

### Behind a proxy — the `Secure` decision reads the effective scheme

The `Secure` flag answers "did the client reach us over HTTPS?", and behind a TLS-terminating proxy the
transport cannot answer that: the app-facing hop is plaintext, so a wire-scheme check would issue the
session id without `Secure` on every proxied HTTPS request, and the browser would later send it over any
plaintext request to the same host (#1050, defect D7). The cookie therefore reads
`context.EffectiveScheme` from `Assimalign.Cohesion.Http.Forwarded` — owner decision 3 in
`docs/programs/HTTP_WEB_PROGRAM_PLAN.md` §7.4: consumers read the effective values; nothing rewrites the
request.

- **With `UseForwardedHeaders`** registered ahead of the first session access, a trusted proxy's
  `https` (RFC 7239 `proto` or `X-Forwarded-Proto`) marks the cookie `Secure`.
- **Without it** — or from a peer outside the forwarded-headers trust model — the effective scheme is the
  transport-derived scheme, so a client that sends `X-Forwarded-Proto: https` itself changes nothing.

The package never parses forwarding headers; which proxy is believed is `Web.ForwardedHeaders`' trust
model. The decision is taken when the cookie is established (first access or regeneration), so the
forwarded-headers middleware must have run by then — registering it first satisfies that.

## Error model

The package defines no exception root; it throws framework `InvalidOperationException` /
`ArgumentException` for misuse (sessions not enabled, regeneration after the head started, non-positive
idle timeout, a cookie name outside `token`, or a cookie path outside `path-value`). All three option
checks run in `UseSessions`, at composition. Each cookie-option failure is an `ArgumentException`
with `ParamName` `configure`: its message names the full option, the cookie constructor's or setter's
exception is preserved as `InnerException`, and neither message echoes the invalid value. No middleware
is registered on failure. Store I/O surfaces whatever the `IHttpSessionStore` implementation throws.

## Breaking behavior since 10.0.0-preview.1

In 10.0.0-preview.1, `UseSessions` accepted an invalid `CookieName`; `HttpCookie` rejected it only when
an application established a session cookie during a request. Since #1416 both `UseSessions` overloads
reject it during composition, before registering middleware. Applications must configure a non-empty
RFC 6265 `token` name. Defaults and other valid names are unchanged; applications that supplied an
invalid name now fail at composition instead of failing cookie-establishing requests.

Since #1405, a new session first accessed after the response head starts reports a throttled
Warning `SessionStateLost` when `IHttpStoredSession.IsModified` is true. This is a diagnostic
behavior change from 10.0.0-preview.1 under decision 15: warning-level monitoring now observes
discarded modifications. Read-only orphans remain Verbose-only; cookies, persistence, and public
contracts are unchanged.

## AOT posture

No reflection, no dynamic serialization, no runtime code generation. Ids use `RandomNumberGenerator` +
`Base64Url`; framing rides `HttpSessionSerializer`; the middleware is plain delegate composition.
Registration is dependency-free — options are captured at builder time, no service container, no
configuration binding, no request-time service location — in keeping with the feature-package rules.

## Non-goals

- **Concrete distributed backends.** This package ships the middleware over the store *seam* with the
  in-memory default; a distributed cache / key-value / database store is a follow-up adapter that
  implements `IHttpSessionStore` (see the scope-creep candidates below).
- **A builder (`Add*`) verb.** Sessions are a global pipeline concern; there is nothing to seed at build
  time beyond the captured options, so only the `UseSessions` pipeline verb ships.
- **`SameSite` configurability.** `Lax` is fixed as the secure default; a configurable mode can layer on
  additively if a concrete need arises without disturbing the contract.
- **Owning DI/config/logging.** Feature packages never take the hosting module (`COHRES001`); the store
  is supplied to `UseSessions`, not resolved from a container.

## Scope-creep candidates (recorded, not taken)

- **Distributed store adapters.** The natural first adapter is over the `Database.KeyValuePair` resource
  (now exists with etag/CAS), then a `libraries/Cache` distributed contract when it matures, then a
  `Database.Cache` resource. Each is a package implementing `IHttpSessionStore` honoring last-commit-wins
  (its stronger CAS is not required by, and not exposed through, this seam).
- **Configurable `SameSite` / cookie domain** on `HttpSessionOptions`, if a cross-subdomain or
  strict-CSRF deployment needs it.

## Testing

Unit tests drive `SessionMiddleware` over an in-memory `IHttpContext` double (cookie posture, HTTPS
`Secure`, lazy no-cookie-when-untouched, load-without-reissue, id regeneration, the head-committed
guard, and the not-enabled / no-session error paths). End-to-end tests over `WebApplicationTestFactory`
(in-memory HTTP/1.1) prove a session round-trips across requests through the session-id cookie, that an
untouched request sets no cookie, and that a cookie-less client starts an independent session.

`tests/SessionForwardedTests.cs` covers the proxy behavior with the real forwarded-headers middleware
(the test project references `Web.ForwardedHeaders`): end to end, with the factory's in-memory peer
trusted as a local transport, a forwarded `https` yields a `Secure` session cookie and a spoofed
`X-Forwarded-Proto` without the middleware does not; at the unit level, a plaintext request from a
trusted proxy address (the double's connection) and from an untrusted one pin the trust dependency.

`tests/SessionPipelineExtensionsTests.cs` drives `UseSessions` over a capturing pipeline builder: a
cookie path outside `path-value` throws at registration and registers no middleware; in-grammar paths,
the percent-encoded form included, register it. `tests/SessionCookieNameTests.cs` covers both overloads'
cookie-name check: null, empty, whitespace, separators, control and non-ASCII characters fail before
registration, and names containing all token punctuation pass. The errors name the option, preserve the
cookie constructor's exception, and do not echo the value.

## Store failure ownership (#1436)

`Assimalign.Cohesion.Http.Sessions` owns store load and commit failure reporting through
its internal same-named EventSource. Web.Sessions deliberately does not repeat those reports.
The HTTP source reports exception types only because store messages can contain session ids
or values. It also reports failures of the idle refresh performed instead of an unchanged
payload commit. The original exception continues to propagate to the caller.

`IHttpStoredSession.IsModified` now exposes the built-in modification flag. Its default
interface implementation returns `true`, preserving custom implementations that commit every
time. Built-in sessions keep skipping unchanged payload writes while renewing the idle window;
this property does not change read-only expiration behavior.

## Diagnostics

The internal `WebSessionEventSource` is named `Assimalign.Cohesion.Web.Sessions`.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | SessionOrphaned | Verbose | reason (`ResponseStarted`), requestId |
| 2 | SessionStateLost | Warning (throttled) | reason (`ResponseStarted`), requestId, suppressedCount:int |

`orphaned-sessions` and `lost-sessions` are exact PollingCounter totals, maintained even when
nobody listens. Every orphan increments `orphaned-sessions` once at the middleware's post-pipeline
commit check and emits `SessionOrphaned` when Verbose is enabled. If the managed session's
`IHttpStoredSession.IsModified` is true, the same check also increments `lost-sessions` and reports
`SessionStateLost`. Reading `context.Session` materializes an empty new session, so a cookieless
read after the head commits is an orphan without lost state: it remains Verbose-only and does not
increment `lost-sessions`.

The loss Warning is limited to once per second across the process; the next emitted Warning's
`suppressedCount` reports the losses suppressed by that throttle. The loss counter still counts
every modified orphan. No session id, cookie, key, or value is written. The request id comes from
`IWebRequestIdFeature` inside the enabled check and, for the Warning, after the throttle admits it.
Http.Sessions owns load/commit failures (see above), with no duplicate report here.

Since 10.0.0-preview.1, diagnostics become observable but cookie and persistence behavior are
unchanged. The Web.Server dependency provides request correlation and adds
no hosting dependency or framework member.

## Exception messages (#1467)

Exception text lives in `src/Properties/ErrorMessages.resx`. The shared build targets generate
an internal `ErrorMessages` accessor in this project's root namespace under the intermediate
output directory; no designer is checked in. Formatted messages use
`string.Format(CultureInfo.CurrentCulture, ErrorMessages.<Name>, ...)`. Resource extraction
preserves the existing exception text and input-disclosure rules, including any diagnostic
type-only exclusions. EventSource templates, log messages and response bodies retain their
existing ownership. This is a conventions change with no public API or behavior break against
10.0.0-preview.1.
