# Assimalign.Cohesion.Web.Results — Design

`Assimalign.Cohesion.Web.Results` owns the Web pipeline's **error-response**
surface: the RFC 9457 `ProblemDetails` model, an AOT-safe `application/problem+json`
writer, the last-chance **exception-boundary** middleware, the **status-code-pages**
middleware, and the builder-time `UseXxx()` verbs that register them. It is the
first real feature project in the Web middleware lane (issue #776, Lane E) and sets
the middleware + pipeline-registration pattern the other Lane E items reuse.

## Why this project exists (the hazard it removes)

Before this, the Web pipeline was unguarded: `WebApplicationPipeline` invoked the
middleware chain with no `try`/`catch`, and the terminal middleware silently
returned `Task.CompletedTask`. Two concrete failures fell out of that:

- **An unhandled middleware exception escaped into the transport.** The server's
  per-connection isolation boundary (`WebApplicationServer.ServeConnectionAsync`,
  #762) caught it and *aborted the connection* — the client got a dropped
  connection, not a response, and any half-written body or internals were at the
  mercy of whatever the middleware had already emitted. A production web server
  cannot ship without a last-chance boundary that turns a fault into a safe,
  bodyless-of-internals **500**.
- **A fall-through request produced an empty 200.** Nothing translated "no
  middleware handled this" into a **404**.

This project closes both, and does it with the exact seams Cohesion's philosophy
prescribes: a typed `IHttpFeatureCollection` feature to expose the caught
exception, builder-time handler-chain composition (never request-time service
location), and a hand-rolled `Utf8JsonWriter` instead of a reflection serializer.

## What lands where (and why)

The pieces are split across three assemblies. The split is deliberate and the
reasoning is recorded here because it is the kind of thing a future reader would
otherwise re-litigate.

| Piece | Assembly | Rationale |
| --- | --- | --- |
| `IHttpExceptionFeature` | `Assimalign.Cohesion.Web` (core) | A neutral pipeline-feature contract carrying the caught **application** exception. Kept out of `libraries/Http`: the protocol core deliberately classifies only *wire-level* failures and carries no app-exception concept. Placed in Web **core** (not this project) so any Web middleware — diagnostics, custom error pages — can read it without depending on Web.Results. |
| `ProblemDetails`, `IProblemDetailsWriter`, writer, `IExceptionHandler`, options, both middleware, the `UseXxx()`/`WriteProblemDetailsAsync` extensions | `Assimalign.Cohesion.Web.Results` (this) | The whole ProblemDetails + boundary feature. Self-contained so an app opts in by adding one package. |
| The 404 terminal fallback + `WriteProblemDetailsAsync` at the pipeline end | `Assimalign.Cohesion.Web.Hosting` | The terminal is baked into `WebApplication.Build` (the one always-installed middleware). Web.Hosting takes a one-way reference on Web.Results for the model + writer. |

**No `Web.ProblemDetails` micro-package.** `docs/SERVICE_LAYER_DESIGN.md` once
floated a dedicated `Assimalign.Cohesion.Web.ProblemDetails` project. It is
deliberately *not* created — it would violate the lean-dependency-tree rule for a
model + a 150-line writer. RFC 9457 is application-layer, so nothing goes in
`libraries/Http` either.

**`UseExceptionHandler()` lives here, not in Web.Hosting.** The issue text placed
the "builder-time `UseExceptionHandler`/handler-chain wiring" in Web.Hosting, but
the middleware itself lands in Web.Results. Putting the registration verb in the
same assembly as the middleware (mirroring `Web.Forms.UseForms()`) keeps the
middleware `internal`, keeps the dependency one-way (`Web.Hosting → Web.Results`,
never a cycle), and makes the feature self-contained — the reusable seam the other
Lane E packages copy. Web.Hosting's share of the wiring is the always-on 404
terminal fallback in `WebApplication.Build`. The `UseXxx()` extensions are declared
in the shared `Assimalign.Cohesion.Web` namespace (again mirroring `UseForms()`) so
one `using Assimalign.Cohesion.Web;` surfaces every registration verb; the model and
writer stay in `Assimalign.Cohesion.Web.Results`.

## The problem+json writer — AOT-safe by construction

`ProblemDetailsJsonWriter` renders a `ProblemDetails` straight through
`Utf8JsonWriter`, following the `OpenApiJsonWriter` precedent. It walks the five
standard members and the extension bag explicitly — there are no
`[JsonPropertyName]` attributes on the model and no reflection-based (de)serializer
anywhere, so `IsAotCompatible=true` holds with zero special handling.

- **`type` always emitted**, defaulting to the reserved `"about:blank"` (RFC 9457
  §4.2); the other four standard members are omitted when null.
- **Extensions are constrained** to an AOT-safe, JSON-shaped set: `null`, `bool`,
  the CLR numerics, `string`, nested string-keyed maps
  (`IEnumerable<KeyValuePair<string, object?>>` → object), and sequences
  (`IEnumerable` → array), recursively. The type switch is a **closed allow-list**,
  not a reflection probe. Anything outside it degrades to its `ToString()` form
  rather than throwing — critical because the writer runs inside the last-chance
  boundary and must never fault.
- **Reserved-key protection.** An extension whose key collides with a standard
  member (`type`/`title`/`status`/`detail`/`instance`) is skipped, so a stray
  extension can never emit a duplicate JSON property.
- **The safe default encoder is kept** (not `UnsafeRelaxedJsonEscaping`): it
  HTML-escapes characters that would be dangerous if a problem+json body were
  mis-rendered as HTML, which matters because `detail` can echo
  request-influenced text.

## Error model — the exception boundary

`UseExceptionHandler()` installs `ExceptionHandlerMiddleware` as a `try`/`catch`
around the rest of the pipeline. On a fault it:

1. **Rethrows a cancelled-request `OperationCanceledException`** (via a `when`
   filter on `context.RequestCancelled`) — a cancelled request is a clean drain,
   not an error, and the server observes it as such.
2. **Publishes the caught exception** as `IHttpExceptionFeature` on
   `context.Features`, so handlers/diagnostics/custom pages can read it.
3. **Evaluates the diagnostics-suppression callback** and, when it returns true,
   flags the exchange via `context.Items[ExceptionHandlerOptions.DiagnosticsSuppressedItemKey]`
   — the parity seam for .NET 10's `SuppressDiagnosticsCallback`, honored by a
   future diagnostics middleware (#794).
4. **Best-effort resets the response** — a seekable body has not flushed to the
   wire and is truncated + its headers cleared; a non-seekable body already
   carrying content markers cannot be reset, so the boundary re-throws to the
   server's per-connection abort (mirrors ASP.NET's "response has already started"
   bail).
5. **Runs the ordered `IExceptionHandler` chain**; the first to return `true` owns
   the response. A handler that *throws* is swallowed so it cannot defeat the
   boundary.
6. **Falls back** to a safe problem+json at `options.StatusCode` (500 by default).

### Deliberate broad catch

The boundary catches bare `Exception`. This departs from the AGENTS.md
"catch specific exceptions" rule and is annotated in source as a documented
deviation: a *last-chance exception boundary* must intercept every fault to keep it
from escaping into the transport — the same pattern, and the same justification,
as the server's `ServeConnectionAsync` isolation boundary. Narrowing it would
reintroduce exactly the leak this project removes.

### Developer detail is off by default

`IncludeDeveloperDetails` defaults to `false`: the fallback 500 carries no
`detail` and no exception text, so internals never leak in production. A host turns
it on (typically only in the Development environment — `builder.Environment.IsDevelopment()`)
to echo the message and full exception string in an `exception` extension.

## Status-code pages

`UseStatusCodePages()` installs a post-`next` middleware that fills a **bodyless**
4xx/5xx terminal response with problem+json (or defers to a custom `Responder`). It
acts only when the response is genuinely bodyless (no `Content-Type`, zero/absent
`Content-Length`, empty seekable body), so it never clobbers a response a handler
already wrote. `UseStatusCodePagesWithProblemDetails()` is the named, explicit form
of the default.

## Builder-time composition, no request-time service location

Every option — the handler chain, the developer-detail toggle, the fallback status,
the suppression callback, the status-code responder — is composed once inside the
`UseXxx(configure)` callback and captured by the middleware instance. Nothing is
resolved from a service provider per request. This is the Lane E guardrail: pipeline
extensibility flows through builder-time composition and typed
`IHttpFeatureCollection` features, not request-time service location. Custom
handlers implement `IExceptionHandler` and are added to `ExceptionHandlerOptions.Handlers`
at build time.

## Relationship to the pipeline terminal

The 404 terminal fallback (in Web.Hosting's `WebApplication.Build`) and this
project's exception boundary are complementary, non-overlapping halves:

- **Terminal → 404**: the pipeline ran to its end and *nobody produced a response*
  (silent fall-through).
- **Boundary → 500**: a middleware *threw*.

A middleware that throws never reaches the terminal, so the two never contend. Both
render through the same `ProblemDetails` writer.

## AOT posture

`IsAotCompatible=true` holds with no special handling. Serialization is an explicit
`Utf8JsonWriter` walk over a closed type switch; the middleware use `Task`,
`ValueTask`, feature-collection lookups, and stream writes — no reflection, no
runtime codegen, no `Assembly.LoadFrom`, no reflection-based (de)serialization.

## Testing

`tests/` covers, with xUnit + Shouldly against local pipeline/context doubles: the
writer (standard members, `about:blank` default, optional-member omission,
scalar/array/nested-map extensions, reserved-key skip, output-form agreement); the
boundary (500 fallback, typed-feature publication, developer-detail on/off, handler
chain order + first-wins + faulty-handler fall-through, custom fallback status,
partial-response reset, cancelled-request rethrow, suppression flag); the
status-code pages (bodyless fill, non-bodyless left untouched, success left
untouched, custom responder); and the `WriteProblemDetailsAsync` response
extension. The 404 terminal fallback is covered in `Web.Hosting`'s test suite.

## Non-goals

- **A model binder / validation-error mapping.** Mapping `ObjectValidation`
  failures to problem+json validation extensions is sequenced behind the
  endpoint-binding work (#28/#29) and belongs to the Web.Api / source-generation
  layer, not here.
- **A `Web.ProblemDetails` micro-package** — explicitly rejected (see above).
- **Request-mutation / re-execute status pages.** The status-code middleware writes
  problem+json or defers to a responder; a full ASP.NET-style *re-execute the
  pipeline against an error path* mode waits on the request-mutation seam decision
  (#24/#25).
- **Logging.** The boundary sets a typed feature and a suppression flag; emitting
  logs is the diagnostics middleware's job (#794), keeping this project free of any
  logging dependency.
