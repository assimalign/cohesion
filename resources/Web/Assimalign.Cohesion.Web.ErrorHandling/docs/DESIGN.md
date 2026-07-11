# Assimalign.Cohesion.Web.ErrorHandling — Design

The error-handling half of the #864 pipeline design (the other half is the `Web.Serialization`
content registry). The Web area is **middleware-first** — handlers write responses imperatively,
and there is no result-carrier channel through which errors could travel as values (IResult was
withdrawn pre-merge on PR #887). Faults therefore travel the only way they can in a middleware
pipeline: as exceptions. This package is the seam that turns them back into responses — owned by
the application, not by whichever feature happened to throw.

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
   presentation. The boundary catches, resolves `IHttpErrorHandlingFeature`, and delegates the
   response.
3. **The server's exception isolation (#762, `Web.Hosting`)** — infrastructure protection: an
   exception that escapes even the boundary (or a fault in the boundary/handlers themselves)
   must not kill the connection loop. The server cannot invoke this hook — the hosting-isolation
   rule forbids it from referencing this package — and that is by design: its catch is about
   connection survival, not response shaping, and produces no application payload.

`HandleAsync` assumes an **unstarted response**; a boundary that buffers or wraps enforces
that invariant and owns response hygiene (clearing half-set headers/status). The hook cannot
un-send a committed response head, so it does not pretend to.

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

- **The exception boundary, status-code pages, and the 404 terminal** — #881, invoking this
  seam.
- **Retry, compensation, or fault swallowing** — the hook shapes responses; it does not manage
  recovery.
- **Logging/diagnostics of faults** — #794 (`Web.Diagnostics`) territory; handlers may of
  course log, but the seam imposes nothing.
- **Environment-aware developer error pages** — a boundary concern (#881) layered on the same
  hook.
