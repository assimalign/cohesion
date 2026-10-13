# Assimalign.Cohesion.Web.ErrorHandling — Design

The error-handling half of the #864 pipeline design (the other half is the `Web.Serialization`
content registry). The Web area is **middleware-first** — handlers write responses imperatively,
and there is no result-carrier channel through which errors could travel as values (IResult was
withdrawn pre-merge on PR #887). Faults therefore travel the only way they can in a middleware
pipeline: as exceptions. This package is the seam that turns them back into responses — owned by
the application, not by whichever feature happened to throw.

The package carries two layers. The **`OnError` hook** (#864) is the composition surface —
`builder.Services.AddErrorHandling(errors => errors.OnError(...))` registers the handler chain and the
terminal problem+json default.
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
- **`AddErrorHandling(errors => errors.OnError(...))`, not `builder.OnError(...)` directly.** A
  root-level `OnError` verb would read as repeatable subscription (`+=`), but with name-keyed
  features each root call would compose a fresh hook that silently replaces the previous one —
  dropping earlier handlers with no diagnostic. Keeping `OnError` on the `ErrorHandlingBuilder` the
  callback receives (the `AddAuthentication` idiom) makes repetition safe where it is safe and
  impossible where it is not.
- **A component integration on `builder.Services` (owner decisions 34 and 35, 2026-10-09, #1380).**
  The package declares `[assembly: ComponentIntegration]` over `ErrorHandlingBuilder.Build`
  (`src/Properties/ComponentIntegrations.cs`); the generator projects the builder template onto
  `IServiceProviderBuilder` in the application's compilation, so the package takes no
  dependency-injection reference. The template constructs the builder (public, parameterless), runs
  the callback, calls `Build()`, and registers the hook as an `IHttpFeature` singleton, the only
  lifetime `Web.Hosting` accepts. `Build()` hands the feature a snapshot of the handlers, so the
  chain is immutable and the request path takes no lock; until #1380 the verb was an
  `extension(IWebApplicationBuilder)` member that returned the builder, and the feature read a
  copy-on-write array the builder kept mutating. A composition surface without a container
  registers `new ErrorHandlingBuilder().OnError(...).Build()` through
  `IWebApplicationBuilder.AddFeature`.
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
   must not kill the connection loop. The server does not invoke this hook — it does not reference
   this package (the hosting-isolation rule forbade it until 2026-10-09) — and that is by design: its catch is about
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
  The abort trips the generic `IHttpContext.RequestCancelled` signal. Web.Hosting checks it even
  when the boundary returns normally (#1411), classifies the exchange as cancelled, skips
  response-completion callbacks and records `error.type=request_canceled`. The original fault
  still reaches the boundary's diagnostic hook. Compared with 10.0.0-preview.1, callbacks no
  longer run after this aborted response; no new feature or Web-root contract is required.
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

An `OnError` handler can write a problem response with an unregistered status accepted by core
Http (#1414). `ProblemDetails.FromStatus` retains its generic title: `Error` for 499 and
`Internal Server Error` for 599. The Stage 14 wave 1 boundary characterization covers both after
the boundary clears a partial response, preserving the numeric status and title and recording the
original application fault. It pins the integrated behavior without changing production code;
the boundary's terminal fallback remains 500.

### The diagnostics hook and its suppression

The boundary exposes an `OnException` observation hook (fault → logging/metrics/tracing, dependency-free
so the boundary needs no logging stack) invoked for each caught fault. `SuppressDiagnosticsCallback` is
the Cohesion parity for .NET 10's `ExceptionHandlerOptions.SuppressDiagnosticsCallback`: a predicate that
marks a fault *expected*, skipping `OnException` for it while the fault is still handled and a response is
still produced. The predicate is evaluated once; the same result routes the source's `PipelineFault`
Error event to `ExpectedPipelineFault` at Verbose, without changing the fault count. A throwing
predicate is treated as unsuppressed. Both a throwing predicate and a throwing `OnException` raise
`DiagnosticsCallbackFailed`. A throwing `OnException` is swallowed —
observation must never defeat response rendering — whereas a throwing `IErrorHandler` propagates; the
distinction is deliberate (an observer only watches; a handler owns the response).

### Client faults are outcomes, not faults (#1340)

A request body that breaks its framing (a malformed chunk size or trailer section) or a configured
limit (the body-size cap, the minimum data rate, the trailer-section bounds) fails the application's
read with an `InvalidDataException` or an `IOException`. That
exception is the transport's answer to the client's bytes: the transport latches a `400`, `413`, `408`
or `431`, sends it in place of any
response that has not started, and closes the connection. Before #1340 the boundary treated it as a
fault like any other: `OnException` ran and a `500` problem was staged, which the transport then
replaced. Any client could make the application's fault observer fire at will.

The server reports such an exchange through `IWebClientFaultFeature` (`Web.Server`; the default Web
server installs it on a request that can have a body, on every version since #1378). On HTTP/2 and
HTTP/3 a malformed body resets the stream instead of drawing a status; the feature reports `400`, and the
reset also fires `RequestCancelled`, so a pipeline that meets the cancellation rather than the read's
failure passes the boundary as a cancelled request, never as a fault. When the feature reports a status
the boundary:

- still publishes `IHttpExceptionFeature`, so a reader can see what the read threw;
- skips `OnException`, as `SuppressDiagnosticsCallback` would, without the application writing a
  predicate for it;
- skips the `OnError` handlers and the terminal, because a client fault is an outcome on the line drawn
  above, and stages the transport's status with no headers and an empty body. The transport then keeps
  that response, since its status is the one it sends, and every reader downstream of the boundary (the
  HTTP access log, the server's telemetry) sees the status that reaches the wire;
- still aborts a started response, as for any other fault.

The classification is by exchange state, not by exception type, so it survives a reader that wraps the
read's exception. A custom server that does not install the feature leaves the boundary as it was. The
reference to `Web.Server` is a feature-to-feature one the Web dependency rule allows; the package is
already an `App.Web` member.

### A connection lost mid-body cancels the exchange (#1439)

Owner decision 45 applies on HTTP/1.1 and HTTP/2 as well as HTTP/3: if the connection closes or
resets before the dispatched request body completes, `RequestCancelled` fires and the body read
throws `OperationCanceledException` carrying that exchange token. `IWebClientFaultFeature.StatusCode`
remains `null`. The boundary rethrows the cancellation without invoking `OnException` or staging
an error response, and Web.Hosting records `request_canceled` without an Error log.

This changes observable behavior from 10.0.0-preview.1 under decision 15: a reader can no longer
rely on a body cut short surfacing as `EndOfStreamException` or another transport exception.
It also supersedes #1340's HTTP/1.1 incomplete-body `400`; malformed chunk syntax and configured
request-body limits retain their client-fault status.

### A response abandoned by its client is cancellation (#1432)

The transport latches `IHttpExchangeControl.ResponseAbortedByClient` when it reclaims a slow reader
under the minimum response data rate or the peer resets or abandons a response. The default host
publishes that report through `IWebResponseClientFaultFeature` in `Web.Server` on every exchange,
including a bodyless GET. The feature reads the latch on each access and remains true once set.

The exception boundary reads it before its ordinary cancellation filter. When true, any downstream
exception is normalized to `OperationCanceledException` carrying the exchange's `RequestCancelled`
token and retaining the caught exception as its inner exception. It cancels the exchange, publishes
no `IHttpExceptionFeature`, invokes neither `OnException` nor `OnError`, and stages no error response.
Web.Hosting records `request_canceled`, skips completion callbacks and emits no Error log. A content
adapter that wraps the transport failure in `InvalidOperationException` gets exactly the same result:
classification uses the transport's report, not an exception type or an inner-exception walk.

An absent or false report keeps the existing paths: an `OperationCanceledException` with a fired
request token is cancellation; an ordinary application failure still reaches the fault observer and
error handlers. Application cancellation alone does not latch a response client fault. No new
Web-root contract, assembly reference, reflection or dependency on transport internals is needed.

**Compatibility with 10.0.0-preview.1.** Peer-abandoned responses now skip the fault observer even
when a writer wraps the failure; preview.1's boundary could recognize only the cancellation exception.
This behavior change follows decision 15. HTTP/1.1 already enforced its configured response rate in
preview.1; HTTP/2 and HTTP/3 response-rate reclamation arrived later in #1384.
The new Web.Server feature is additive; the default member on `IHttpExchangeControl` is the
public-interface addition explicitly sanctioned by #1432.

`ResponseClientFaultTests` pins classification with an arbitrary exception and an explicit true or
false feature. Web.Hosting's integration suite uses `UseErrorHandling()` around streamed GETs over
HTTP/1.1, HTTP/2 and HTTP/3: a stalled response and a peer reset each produce a wrapped write failure,
then cancellation with the exact exchange token, no fault hooks and no Error log. The false-latch
unit test preserves existing application-fault behavior.

## Status-code pages and the 404 terminal (#881)

`UseStatusCodePages()` installs `StatusCodePagesMiddleware`, which runs after `next` and upgrades a
**bodyless** `4xx`/`5xx` terminal response into a body — RFC 9457 problem+json by default, or a custom
responder. It acts only when the response is genuinely bodyless (no `Content-Type`, no positive
`Content-Length`, no buffered body) and unstarted, so it never clobbers a body a handler already wrote or
a head already committed.

Its motivating source is the **pipeline's bodyless 404 terminal**. The silent `Task.CompletedTask`
terminal in `WebApplication.Build` (which returned an empty `200` for any unhandled request) now sets a
bodyless `404 Not Found` when the response reaches it untouched (still `200`, no body, no `Content-Type`,
no `Location`). That terminal is **`Web.Routing`'s `WebApplicationTerminal`** (it lived in
`Web.Hosting`, then in the Web root (#1056), until #1379 moved it beside `IWebEndpointFeature`, the
endpoint it runs). `Web.Hosting`'s application pipeline and every `Map`/`MapWhen` branch end in it,
and it stays payload-free: neither `Web.Hosting` nor `Web.Routing` references `Web.ProblemDetails` (or
this package). The resource hosting-isolation rule (COHRES002) forbade the runtime that reference when
this was designed, and since the 2026-10-09 relaxation the reference would still ship in every
framework that carries the module. So the terminal can only set the status — this package's opt-in
status-code-pages middleware is what turns it into problem+json. A middleware that deliberately produces an empty `200` must be
terminal (not chain to `next`); a bodyless-`200` fall-through is read as unhandled.

## Homing under the hosting-isolation rule

The issue posed the choice: an area-root seam or a feature package. The default handler decides
it — it renders `Web.ProblemDetails`, and the area root must not reference feature packages, so
a root-homed hook would either lose its default or invert the dependency direction. This is a
feature package referencing `Http`, `Http.Streaming`, `Web`, `Web.ProblemDetails` and `Web.Server`
(for `IWebClientFaultFeature`, #1340, and `IWebResponseClientFaultFeature`, #1432); `Web.Hosting` references
none of it (COHRES002 forbade that until 2026-10-09), and applications receive it through the `App.Web` shared framework.
The #881 boundary middleware — a pipeline feature, not runtime code — consumes it as an ordinary
cross-feature reference.

## Error model

This package handles others' exceptions and defines none of its own; misuse of the composition
surface is plain `ArgumentNullException`. `HandleAsync` guards its arguments and otherwise lets
everything a handler throws propagate (see above).

## AOT posture

Nothing dynamic: sealed internals, delegate/interface dispatch, and the payload rendering is
`Web.ProblemDetails`' hand-rolled `Utf8JsonWriter` path. No reflection, no source generation.

## Diagnostics

The internal `ErrorHandlingEventSource`, named `Assimalign.Cohesion.Web.ErrorHandling`, owns
application-fault reporting. Enable it by name with an EventListener or `dotnet-trace`, or forward
it to configured sinks through `Assimalign.Cohesion.Logging.EventSource`. No logging dependency
or public diagnostics surface is added. NativeAOT applications must enable `EventSourceSupport`.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | `PipelineFault` | Error | `exceptionType`, `exceptionMessage`, `responseStarted:bool`, `requestId` |
| 2 | `ExpectedPipelineFault` | Verbose | Same as event 1 |
| 3 | `ErrorHandlerFailed` | Error | `handlerIndex:int` (zero-based), `handlerType`, `exceptionType`, `exceptionMessage`, `requestId` |
| 4 | `DiagnosticsCallbackFailed` | Warning | `callback` (`OnException` or `SuppressDiagnosticsCallback`), `exceptionType`, `exceptionMessage`, `requestId` |
| 5 | `ResponseAbortedByPeer` | Verbose | `exceptionType`, `exceptionMessage`, `requestId` |

The boundary classifies the original fault when caught and emits event 1 or 2 once, including
after headers commit. It snapshots `responseStarted` before diagnostics run, evaluates suppression
once, then reports before invoking `OnException` or the error handlers. Cancellation or a client
fault encountered in those callbacks or handlers cannot retroactively hide the original fault.
An application failure in a streamed `OnStarting` callback is still reported when the response
starting feature cancels the exchange before rethrowing it. Request-body client statuses,
response-client latches and the actual request-cancelled `OperationCanceledException` path are
excluded; a cancelled token alone is not evidence of client causation. An
`OperationCanceledException` whose exchange token is not cancelled remains an application fault.
Handler failures in both the boundary and standalone `IErrorHandlingFeature.HandleAsync`
raise event 3 through a false exception filter; the original
exception propagates unchanged. A local async invocation captures synchronous throws first, so
the handler's `finally` blocks finish before the filter checks transport state. Only cancellation
exceptions with a cancelled request token and explicitly latched client failures are excluded;
ordinary application exceptions remain reportable even if a handler cancels during unwind.

Event 5 is raised from the boundary's response-client-fault catch (#1432) and from the false
handler-failure filter used by both the boundary and standalone `HandleAsync`. A peer failure
while writing an error response propagates unchanged from the handler path and emits Verbose
`ResponseAbortedByPeer`, never `ErrorHandlerFailed`. The earlier application fault remains
reported and counted by the boundary; the subsequent peer failure increments neither counter.
An internal immutable marker on the exchange deduplicates event 5 when a surrounding boundary
observes the same failure, even if a wrapper changed the exception. The marker is installed only
when Verbose is enabled and adds no public feature contract or disabled-listener work.
It uses `IWebResponseClientFaultFeature.ResponseAbortedByClient`, with no exception-type test,
inner-exception inspection, or second peer classification. Slow readers, resets and STOP_SENDING
remain cancellation even when a content adapter wraps the write failure.

| Counter | Kind | Counting rule |
| --- | --- | --- |
| `total-faults` | PollingCounter | One per application fault reported through event 1 or 2, including when no listener is enabled |
| `faults-per-second` | IncrementingPollingCounter | Rate over the same total |
| `aborted-responses` | PollingCounter | One per boundary-initiated abort of a started response; excludes client faults and already-cancelled exchanges |

**Reconciliation with #1411.** A started response that the boundary aborts is a cancelled
exchange, while its originating application fault remains reportable. It increments
`total-faults` and emits `PipelineFault` or `ExpectedPipelineFault` with `responseStarted=true`
before cancellation; `aborted-responses` independently counts the boundary's abort. Its original
exception also reaches the existing `OnException` hook and `IHttpExceptionFeature`. Exchange
completion classification does not change application-fault attribution. A response abandoned
by its peer increments neither counter. Counter fields are maintained with Interlocked whether
listeners are present or absent; the counters themselves are created on first enable. No request
count or Meter duplicates are added.

Application exceptions, including handler and diagnostic-callback failures, are written as their
full type name and **Message**, never stack traces or `Exception.ToString()`. The source does not
read the exception feature's path, query, method, headers, body, or other request content.
Application messages can themselves contain application data; decision 49(d) accepts their
delivery to configured sinks. Request ids come from `IWebRequestIdFeature` (empty if a custom
server omits it), never `Activity.Current`. Payload reads and formatting occur only inside the
appropriate `IsEnabled` check. These distinct application faults are not throttled.

**Compatibility with 10.0.0-preview.1 (decision 15).** A default `UseErrorHandling()` now emits
fault diagnostics, including application exception messages, without an `OnException` registration.
`SuppressDiagnosticsCallback` now governs event 1 as well as the hook. An application whose
`OnException` hook logs a fault and which also enables forwarding logs that fault twice; choose
one reporting path if a single log entry is required. The HTTP error response remains safe by
default, and no public signature changes.

The serial `ErrorHandlingEventSourceTests` cover strict manifest validation, payloads and request
correlation, cancellation/client exclusions, suppression, unchanged handler propagation, peer-abort
deduplication across handler and nested-boundary paths, and exact
counter deltas with and without listeners. Web.Hosting's streamed response tests cover slow readers
and peer resets on HTTP/1.1, HTTP/2 and HTTP/3, including a wrapped write exception. Both suites link
the canonical test-only recorder; fresh counter samples prove peer outcomes leave both totals unchanged.

## Non-goals

- **Retry, compensation, or fault swallowing** — the boundary and hook shape responses; they do
  not manage recovery.
- **HTML developer error pages** — the developer-detail toggle enriches the problem+json *payload*
  (message + exception text); a rendered HTML exception page with source/stack framing is not in
  scope. Custom presentation is a status-code-pages responder or an `OnError` handler.
- **Wire-level failure isolation** — the transport's per-connection survival (Http.Connections, and
  the server's #762 last-resort catch) is a separate layer the boundary sits inside, not a duplicate.

Transport integration tests consume duplex byte pipes through `IDuplexConnection` and HTTP connections through `IHttpConnection`. Their listeners use `AcceptAsync`; HTTP/3 stream fixtures use `IMultiplexedConnectionStream`. These contract names do not alter this package's request/response behavior.

## Exception messages (#1467)

Exception text lives in `src/Properties/ErrorMessages.resx`. The shared build targets generate
an internal `ErrorMessages` accessor in this project's root namespace under the intermediate
output directory; no designer is checked in. Formatted messages use
`string.Format(CultureInfo.CurrentCulture, ErrorMessages.<Name>, ...)`. Resource extraction
preserves the existing exception text and input-disclosure rules, including any diagnostic
type-only exclusions. EventSource templates, log messages and response bodies retain their
existing ownership. This is a conventions change with no public API or behavior break against
10.0.0-preview.1.

The expected rejection or cancellation messages are cached in static readonly fields: `ClientAbandonedResponse`. Their ordinary lifecycle paths perform no resource lookup per exception.
