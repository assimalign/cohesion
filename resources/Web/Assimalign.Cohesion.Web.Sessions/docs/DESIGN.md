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
- **`Secure`** — set only when the request scheme is HTTPS (the transport-derived typed scheme, not a
  header or string), so the cookie round-trips over the plaintext in-memory test transport yet is
  Secure in production HTTPS.
- **Session-scoped** — no `Max-Age`/`Expires`; the cookie clears when the browser session ends, and
  server-side idle timeout governs true expiry.
- **Name / Path** — from `HttpSessionOptions.CookieName` / `CookiePath`.

## Error model

The package defines no exception root; it throws framework `InvalidOperationException` /
`ArgumentException` for misuse (sessions not enabled, regeneration after the head started, non-positive
idle timeout). Store I/O surfaces whatever the `IHttpSessionStore` implementation throws.

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
