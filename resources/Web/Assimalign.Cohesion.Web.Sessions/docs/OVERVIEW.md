# Assimalign.Cohesion.Web.Sessions — Overview

Per-request HTTP session support for the Cohesion Web pipeline. The package wires the async session
store seam from `Assimalign.Cohesion.Http.Sessions` into the Web middleware model: the middleware
`UseSessions` places establishes a hardened session-id cookie, installs a store-backed session on the
exchange lazily on first access, and commits it after the pipeline unwinds. Concrete distributed
backends are deferred — the middleware runs unchanged over any `IHttpSessionStore`, defaulting to the
in-process store.

## Scope

- **`AddSessions(sessions => ...)` and `UseSessions()`** — the registration verb on `builder.Services`
  configures the cookie, the idle timeout, and the store (`SessionsBuilder.Options`,
  `SessionsBuilder.UseStore`), and the pipeline verb places the middleware ("Add configures, Use
  places"). Without `UseStore` the middleware uses an in-process `InMemoryHttpSessionStore`; any
  `IHttpSessionStore` (a distributed adapter) gives a multi-instance deployment sticky-session-free
  affinity with no other pipeline change. Without a registration, `UseSessions()` places the defaults,
  and when the application registers sessions more than once, the last registration wins.
- **Session-id cookie establishment** through the hardened `Http.Cookies` model — `HttpOnly` (from
  options), `SameSite=Lax`, `Secure` whenever the effective scheme is HTTPS (the transport-derived typed
  scheme, or a trusted proxy's forwarded scheme when `UseForwardedHeaders` runs first), session-scoped
  (no `Max-Age`/`Expires`). Cookie name/path/HttpOnly come from `HttpSessionOptions`.
- **Lazy lifecycle** — no store I/O and no cookie until the application first touches the session.
  `context.LoadSessionAsync()` materializes, establishes the cookie for a new id, and loads; the
  middleware commits (persist-if-modified, else slide) after `next`. An untouched session performs no
  I/O and mints no cookie.
- **Cryptographically random session ids** (128-bit, URL-safe base64url) plus
  **`context.RegenerateSessionIdAsync()`** — the post-authentication fixation defense (new id, same
  state, old id removed from the store, cookie replaced).
- **Sliding idle expiration** — renew-on-access, driven by `HttpSessionOptions.IdleTimeout` at the
  store level.

## Dependencies

- `Assimalign.Cohesion.Web.Server` — request-id correlation for orphan diagnostics.

- `Assimalign.Cohesion.Web` — the pipeline builder and middleware abstractions the verb and middleware build on.
- `Assimalign.Cohesion.Core`'s `ComponentIntegration` shared source — declares `AddSessions` as a
  component integration the consumer's generator projects onto `builder.Services`; the package takes no
  DI reference.
- `Assimalign.Cohesion.Http` — the HTTP context, feature collection, and transport-derived scheme.
- `Assimalign.Cohesion.Http.Forwarded` — the `EffectiveScheme` read behind the `Secure` decision, which
  falls back to the transport-derived scheme when no forwarded-headers middleware ran.
- `Assimalign.Cohesion.Http.Sessions` — the `IHttpSession` surface, the `IHttpSessionStore` seam, the
  in-memory store, and the frame serializer this middleware composes.
- `Assimalign.Cohesion.Http.Cookies` — the hardened cookie model used to establish the session-id cookie.
- `Assimalign.Cohesion.Http.Streaming` — the response-streaming feature the establishment path checks
  before touching headers (head-committed guard).

It never references `Assimalign.Cohesion.Web.Hosting` (the resource hosting-isolation rule, `COHRES001`).

## Usage

```csharp
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions;

builder.Services.AddSessions(sessions =>
{
    sessions.Options.CookieName = ".Cohesion.Session"; // HttpCookieName; default value
    sessions.Options.CookiePath = "/"; // HttpCookiePath; default value
    sessions.Options.IdleTimeout = TimeSpan.FromMinutes(20);
});

WebApplication app = builder.Build();
app.UseSessions();

app.Use(async (context, next) =>
{
    IHttpSession session = await context.LoadSessionAsync(context.RequestCancelled);

    int views = session.GetInt32("views") ?? 0;
    session.SetInt32("views", views + 1);

    await next(context);
});

// After authenticating a user, rotate the id to defeat session fixation:
await context.RegenerateSessionIdAsync(context.RequestCancelled);
```

`CookieName` and `CookiePath` use `HttpCookieName` and `HttpCookiePath`. Valid strings convert
implicitly; an out-of-grammar string throws `ArgumentException` at assignment with parameter `value`.
`AddSessions` rejects a non-positive idle timeout and `default` cookie options where it registers the
middleware, with an option-specific message and parameter `Options` (`configure` before 2026-10-28).
The empty path is valid. These property types replace `string` from 10.0.0-preview.1 (#1473);
use `.Value` when a caller needs a string-specific member.

For an out-of-process deployment, supply a distributed store adapter — everything else is identical:

```csharp
builder.Services.AddSessions(sessions =>
{
    sessions.UseStore(new MyDistributedSessionStore(...));
    sessions.Options.IdleTimeout = TimeSpan.FromMinutes(30);
});
```

The application owns a store it supplies; the middleware never disposes it.

## Stored-session modification tracking

`IHttpStoredSession.IsModified` reports whether the built-in session needs a payload write.
It returns `true` by default for custom implementations that do not track changes, so existing
stores keep committing. Read-only built-in sessions still refresh their idle timeout.
Store failures can be observed through the `Assimalign.Cohesion.Http.Sessions` EventSource;
see the [design](DESIGN.md#store-failure-ownership-1436) for payload and ownership details.

Establish a new session before starting the response so its cookie can be delivered. If first
access occurs after the head starts, the session is discarded after the request. The
`Assimalign.Cohesion.Web.Sessions` EventSource reports a modified orphan with the throttled
Warning `SessionStateLost` and the exact `lost-sessions` counter; a read-only orphan emits only
Verbose `SessionOrphaned`. See [diagnostics](DESIGN.md#diagnostics) for event payloads and counters.
