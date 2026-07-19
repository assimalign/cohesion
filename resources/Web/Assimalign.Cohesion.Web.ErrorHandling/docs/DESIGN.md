# Assimalign.Cohesion.Web.ErrorHandling — Design

The error-handling half of the #864 pipeline design (the other half is the `Web.Serialization`
content registry). The Web area is **middleware-first** — handlers write responses imperatively,
and there is no result-carrier channel through which errors could travel as values (IResult was
withdrawn pre-merge on PR #887). Faults therefore travel the only way they can in a middleware
pipeline: as exceptions. This package is the seam that turns them back into responses — owned by
the application, not by whichever feature happened to throw.

The package carries two layers. The **`OnError` hook** (#864) is the composition surface —
`AddErrorHandling().OnError(...)` registers the handler chain and the terminal problem+json default.
The **pipeline exception boundary** (#881) is the consumer: `UseErrorHandling()` installs the
middleware that catches faults escaping downstream, publishes them as an `IHttpExceptionFeature`,
and dispatches through the hook; `UseStatusCodePages()` upgrades a bodyless `4xx`/`5xx` terminal
response (including the pipeline's bodyless 404) into a problem+json body. The boundary does not
redesign the chain — it consumes the same `IErrorHandler` registrations.

## The faults-vs-outcomes line (the rule this package enforces)

- An **outcome** is an expected protocol result: an authentication challenge's `401`, a
  router's `404`, a negotiation's `406`, an unsupported media type's `415`. Outcomes are each
  feature's *normal response path* — written imperatively, never thrown. A feature that throws
  to signal an outcome is misusing this seam, and the fix is in the feature.
- A **fault** is a failure of the machinery: the data-protection key ring is unavailable, a
  serialization contract is missing, an upstream connection died. Faults are exceptions —
  area-scoped ones per the repo's error-model rules — and the `OnError` hook is where the
  application decides, once, what any fault looks like on the wire.

The line keeps exceptions out of flow control (outcomes stay cheap and typed) and keeps error
presentation out of feature libraries (no feature invents its own error payload).

## Why-this-not-that decisions

- **A `TryHandleAsync → bool` chain, not a single delegate or an event.** The composition
  question (`what happens when several things register?`) is answered structurally:
  registrations are consulted in registration order, the first to return `true` owns the fault,
  and the terminal default runs only when everyone passes. A single-slot delegate forces every
  application into one mega-handler; event-style multicast has no answer to "who writes the
  response". Registration order = consultation order means *specific handlers register first* —
  the same mental model as exception `catch` clauses.
- **`AddErrorHandling().OnError(...)`, not `builder.OnError(...)` directly.** A root-level
  `OnError` verb would read as repeatable subscription (`+=`), but with name-keyed features each
  root call would compose a fresh hook that silently replaces the previous one — dropping
  earlier handlers with no diagnostic. Keeping `OnError` on the returned
  `ErrorHandlingBuilder` (the `AddAuthentication` idiom) makes repetition safe where it is safe
  and impossible where it is not.
- **Global granularity, not per-feature.** One hook per application. Per-feature error hooks
  would re-scatter error presentation into the features — the exact thing the seam exists to
  centralize. A handler that wants feature-specific behavior branches on the exception type,
  which is what area-scoped exception roots are for.
- **The terminal default is not a registration.** `Handlers` exposes exactly what the
  application registered; the `ProblemDetails` default is unconditional backstop behavior of
  `HandleAsync`. "Overridable" means any registration that returns `true` pre-empts it — there
  is no options type and no default-replacement knob in v1, because a handle-everything
  registration *is* the replacement.
- **The default never leaks fault internals.** `500`, `application/problem+json`,
  `about:blank`, the status phrase — no exception message, no stack, no type name. Developer
  convenience (exception detail in dev environments) is a boundary/#881 concern where
  environment awareness lives, not a payload default.
- **Handler exceptions propagate.** A secondary fault inside a handler is not swallowed or
  chained to the next registration — masking it would hide real breakage. It surfaces to the
  invoking boundary, behind which the server's last-resort isolation still stands.

## Relationship to `WebApplicationServer`'s last-resort isolation

Three layers, outermost last:

1. **Handler-local `try/catch`** — a middleware that can produce a real *outcome* from a
   failure does so itself and never involves this seam.
2. **The pipeline boundary → `OnError` (this package + #881)** — application-owned fault
   presentation. The boundary catches, resolves `IErrorHandlingFeature`, and delegates the
   response.
3. **The server's exception isolation (#762, `Web.Hosting`)** — infrastructure protection: an
   exception that escapes even the boundary (or a fault in the boundary/handlers themselves)
   must not kill the connection loop. The server cannot invoke this hook — the hosting-isolation
   rule forbids it from referencing this package — and that is by design: its catch is about
   connection survival, not response shaping, and produces no application payload.

`HandleAsync` assumes an **unstarted response**; a boundary that buffers or wraps enforces
that invariant and owns response hygiene (clearing half-set headers/status). The hook cannot
un-send a committed response head, so it does not pretend to.

## The exception boundary (#881)

`UseErrorHandling()` installs `ExceptionBoundaryMiddleware` — the pipeline layer-2 fault handler of
the three-layer model above. It wraps everything downstream in a `try/catch` and, on a fault:

- **Publishes `IHttpExceptionFeature`** onto the exchange (the caught exception + the request path) so
  handlers, a diagnostics observer, and custom pages can read the fault without it being re-thrown.
- **Guards against clobbering (no-clobber).** If `IHttpResponseStreamingFeature.HasStarted` reports
  the response head is on the wire, the status and headers are locked and no clean error body can
  replace what a faulted handler began streaming — the only honest answer is a protocol-level abort of
  the one exchange (`IHttpContext.CancelAsync`; the connection survives). This is the same wire-commit
  signal the request-timeout middleware reads. While the response is unstarted, the boundary discards
  the partial response (clears headers, truncates/replaces the body) and writes the error cleanly.
- **Dispatches through the `OnError` chain.** It consults `IErrorHandlingFeature.Handlers` in
  registration order, first-`true`-wins — the same contract `HandleAsync` implements. It consumes the
  chain rather than calling `HandleAsync` blindly only so the **developer-detail toggle** can enrich
  the terminal fallback: with `IncludeDeveloperDetails` off (the default) the terminal is byte-identical
  to the chain's default (500 problem+json, `about:blank`, no detail); with it on, the fallback adds the
  exception message as `detail` and the full text as an `exception` extension. The toggle affects only
  the boundary's own terminal — a registered handler that owns the fault is unaffected.
- **Never masks a handler fault.** An exception thrown by a registered `IErrorHandler` propagates out of
  the boundary to the server's last-resort isolation (layer 3) — the shipped `OnError` semantics. A
  client-cancellation `OperationCanceledException` (request token tripped) is re-thrown as a clean drain,
  not manufactured into an error response.

### The diagnostics hook and its suppression

The boundary exposes an `OnException` observation hook (fault → logging/metrics/tracing, dependency-free
so the boundary needs no logging stack) invoked for each caught fault. `SuppressDiagnosticsCallback` is
the Cohesion parity for .NET 10's `ExceptionHandlerOptions.SuppressDiagnosticsCallback`: a predicate that
marks a fault *expected*, skipping `OnException` for it while the fault is still handled and a response is
still produced. Because the repo carries no `Microsoft.Extensions.Logging`, "suppress error-level logging"
becomes "suppress the diagnostic hook the boundary exposes". A throwing `OnException` is swallowed —
observation must never defeat response rendering — whereas a throwing `IErrorHandler` propagates; the
distinction is deliberate (an observer only watches; a handler owns the response).

## Status-code pages and the 404 terminal (#881)

`UseStatusCodePages()` installs `StatusCodePagesMiddleware`, which runs after `next` and upgrades a
**bodyless** `4xx`/`5xx` terminal response into a body — RFC 9457 problem+json by default, or a custom
responder. It acts only when the response is genuinely bodyless (no `Content-Type`, no positive
`Content-Length`, no buffered body) and unstarted, so it never clobbers a body a handler already wrote or
a head already committed.

Its motivating source is the **pipeline's bodyless 404 terminal**. The silent `Task.CompletedTask`
terminal in `WebApplication.Build` (which returned an empty `200` for any unhandled request) now sets a
bodyless `404 Not Found` when the response reaches it untouched (still `200`, no body, no `Content-Type`,
no `Location`). That terminal lives in **`Web.Hosting`** and must stay payload-free: the resource
hosting-isolation rule (COHRES002) forbids the runtime module from referencing `Web.ProblemDetails` (or
this package), so the runtime can only set the status — this package's opt-in status-code-pages middleware
is what turns it into problem+json. A middleware that deliberately produces an empty `200` must be
terminal (not chain to `next`); a bodyless-`200` fall-through is read as unhandled.

## Homing under the hosting-isolation rule

The issue posed the choice: an area-root seam or a feature package. The default handler decides
it — it renders `Web.ProblemDetails`, and the area root must not reference feature packages, so
a root-homed hook would either lose its default or invert the dependency direction. This is a
feature package referencing `Http`, `Web`, and `Web.ProblemDetails`; `Web.Hosting` references
none of it (COHRES001/002), and applications receive it through the `App.Web` shared framework.
The #881 boundary middleware — a pipeline feature, not runtime code — consumes it as an ordinary
cross-feature reference.

## Error model

This package handles others' exceptions and defines none of its own; misuse of the composition
surface is plain `ArgumentNullException`. `HandleAsync` guards its arguments and otherwise lets
everything a handler throws propagate (see above).

## AOT posture

Nothing dynamic: sealed internals, delegate/interface dispatch, and the payload rendering is
`Web.ProblemDetails`' hand-rolled `Utf8JsonWriter` path. No reflection, no source generation.

## Non-goals

- **Retry, compensation, or fault swallowing** — the boundary and hook shape responses; they do
  not manage recovery.
- **A logging/diagnostics stack** — #794 (`Web.Diagnostics`) territory. The boundary's `OnException`
  hook is a dependency-free observation seam, not a logger; it imposes no sink, format, or category.
- **HTML developer error pages** — the developer-detail toggle enriches the problem+json *payload*
  (message + exception text); a rendered HTML exception page with source/stack framing is not in
  scope. Custom presentation is a status-code-pages responder or an `OnError` handler.
- **Wire-level failure isolation** — the transport's per-connection survival (Http.Connections, and
  the server's #762 last-resort catch) is a separate layer the boundary sits inside, not a duplicate.
