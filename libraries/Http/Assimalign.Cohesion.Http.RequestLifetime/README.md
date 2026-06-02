# Assimalign.Cohesion.Http.RequestLifetime

The writable per-request abort signal for the Cohesion HTTP family. The
protocol core exposes a read-only `IHttpContext.RequestAborted` token; this
package adds the writable side — an `IHttpRequestLifetime` whose `Abort()`
triggers the token and whose `RequestAborted` can be observed or replaced.

## Why a separate package

`RequestAborted` on the context is read-only by design — the wire protocol
does not let application code cancel a request. The ability to *trigger* an
abort (cancel a long-running handler, shed load, react to a downstream
failure) is a runtime-contract concern, so it layers on top of the core as an
opt-in feature rather than bloating `IHttpContext`.

## Surface

| Type | Role |
| --- | --- |
| `IHttpRequestLifetime` | Settable `RequestAborted` token + `Abort()`. |
| `HttpRequestLifetime` | `CancellationTokenSource`-backed implementation (disposable). |
| `IHttpRequestLifetimeFeature` | Carries the lifetime on an `IHttpContext`. |
| `context.RequestLifetime` | Resolves (lazily installs) the lifetime. |
| `context.Abort()` | Aborts the current request. |

## Usage

```csharp
// Observe the abort signal in a long-running handler.
CancellationToken aborted = context.RequestLifetime.RequestAborted;
await DoWorkAsync(aborted);

// Force an abort from elsewhere (load shedding, downstream failure).
context.Abort();
```

`context.RequestLifetime` lazily installs a default `HttpRequestLifetime` the
first time it is read. `Abort()` is idempotent and safe to call after the
request has completed.

## Scope

This package is **standalone**: it provides the feature, implementation, and
context ergonomics with **no transport-layer changes**. The lazily-installed
lifetime is self-contained and is not yet auto-linked to the transport's
`IHttpContext.RequestAborted` — that wiring is a transport concern tracked
separately. See `docs/DESIGN.md` for the lifetime model, the relationship to
`IHttpContext.RequestAborted`, and non-goals.
