# Assimalign.Cohesion.Http.RequestLifetime — Design

The writable per-request abort signal for the Cohesion HTTP family. This
document captures the design intent so future readers do not have to
re-derive it from the code.

## Design intent

The protocol core exposes a **read-only** `IHttpContext.RequestAborted`
token. Application code and middleware sometimes need the **writable** side:
a token they can observe *and* a way to force an abort (cancel a long-running
handler, shed load, react to a downstream failure). This package supplies
that through `IHttpRequestLifetime` (a settable `RequestAborted` token plus
`Abort()`), an `IHttpRequestLifetimeFeature` that carries it on a context, and
ergonomic `context.RequestLifetime` / `context.Abort()` access.

## Lifetime model

`HttpRequestLifetime` is backed by a single `CancellationTokenSource`:

- `RequestAborted` defaults to that source's token, so an observer sees the
  cancellation when `Abort()` runs.
- `Abort()` cancels the source. It is **idempotent** (safe to call repeatedly)
  and a **no-op after `Dispose()`** — a finished request that is aborted again
  should not throw.
- `Dispose()` releases the source and is idempotent.

The token is **settable** because the interface allows replacing it (for
example to substitute a token linked to an external source). The documented
contract: if you replace `RequestAborted`, you own that token's cancellation;
`Abort()` still cancels the internal source. This mirrors the conventional
`IHttpRequestLifetimeFeature` shape where the token and the abort trigger are
both exposed and a host may swap the token.

## Relationship to IHttpContext.RequestAborted

`IHttpContext.RequestAborted` is the **read surface** the protocol core
already provides; this package's `IHttpRequestLifetime` is the **writable
source**. In a fully wired host the transport would create the lifetime and
arrange for `context.RequestAborted` to observe `lifetime.RequestAborted`, so
the two agree. That wiring lives in the transport and is **out of scope** for
this standalone pass — see "Scope" below.

## Context ergonomics

`context.RequestLifetime` lazily installs a default `HttpRequestLifetime`
(wrapped in the internal `HttpRequestLifetimeFeature`) when none is present,
mirroring the lazy-install pattern used by the Cookies and Forms features.
`context.Abort()` is sugar over `context.RequestLifetime.Abort()`. Storage is
the strongly-typed `IHttpContext.Features` collection so middleware can
observe or replace the feature.

## Scope: standalone

This pass delivers the feature, its implementation, and the context
ergonomics — fully functional on their own (`Abort()` fires the token, the
token is observable). It deliberately makes **no transport-layer changes**:
the lazily-installed lifetime is self-contained and is not automatically
linked to the transport's `IHttpContext.RequestAborted` or to a connection's
`ConnectionCancelled`. That linkage is a transport concern tracked separately.

## AOT posture

No reflection, no runtime code generation. A single
`CancellationTokenSource`, a settable token field, and `Interlocked`/`Volatile`
guards for the dispose race. Fully AOT/trim safe.

## Non-goals

- **Transport wiring.** Driving the lifetime from the transport's
  connection-cancelled / per-request abort signal is out of scope here.
- **Timeouts.** This package does not impose request timeouts; a host can
  layer a timer that calls `Abort()`.
- **Disposal orchestration.** Whoever creates a standalone
  `HttpRequestLifetime` owns disposing it. When the lifetime is lazily
  installed via the extension, its disposal follows the host's context
  teardown story (a transport-wiring concern).
