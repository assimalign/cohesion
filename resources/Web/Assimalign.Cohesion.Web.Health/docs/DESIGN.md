# Assimalign.Cohesion.Web.Health — Design

## Design intent

One Web feature owns Cohesion health checks end to end: the application-facing health model, a
builder-time registry, readiness/liveness selection, HTTP pipeline endpoints, and a reflection-free
JSON response writer. Keeping registration and delivery together gives Web applications one small,
explicit API without introducing a DI-specific hosting layer.

## Public application composition

Web application and resource authors can register typed checks, inline probes, or
`Hosting.Health` contributors through Web.Hosting's adapter in the
`Assimalign.Cohesion.Web.Hosting.Health` namespace:

```csharp
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting.Health;

IHealthCheckService health = HealthChecks.CreateBuilder()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy(),
        tags: new[] { HealthTags.Ready, HealthTags.Live })
    .AddContributor(databaseContributor)
    .Build();

pipeline.MapHealthChecks(health);
pipeline.MapReadinessCheck(health);
pipeline.MapLivenessCheck(health);
```

`HealthChecks.CreateBuilder()` is the container-free composition seam. Registrations are mutable
only while composing the builder; `Build()` snapshots them into an immutable
`IHealthCheckService`. The service is supplied to endpoint middleware explicitly, so request-time
service location is unnecessary.

## Hosting contributor integration

The `AddContributor` adapter ships in `Assimalign.Cohesion.Web.Hosting`, keeping the
`Assimalign.Cohesion.Web.Hosting.Health` namespace (owner decision 57, 2026-10-13). That module
references this model and `Hosting.Health`; this package references only the Web root. No Web
root or feature library may reference the hosting module. The adapter is included with hosting,
not supplied by an optional package.

## Status model and aggregation

`HealthStatus` is ordered least to most healthy: `Unhealthy = 0`, `Degraded = 1`, and
`Healthy = 2`. A report's aggregate status is the minimum across its entries. An empty report is
healthy, allowing `/livez` or `/readyz` to remain meaningful when their tag slice has no entries.

Checks run sequentially. A thrown or timed-out check becomes an entry with the registration's
`FailureStatus`; caller cancellation propagates instead of being converted into a health result.
The entry's description is fixed text, `The check threw an exception.` or `The check timed out.`,
never the exception's message. The entry keeps the exception: the one the check threw, or the
`OperationCanceledException` that ended a timed-out check (see "Exception detail" below).

## Request handling

Mapped health middleware:

1. Passes through requests whose path or method does not match.
2. Runs `IHealthCheckService.CheckHealthAsync` with the endpoint predicate and request cancellation.
3. Attaches `IHttpHealthFeature` so other HTTP components can inspect the completed report.
4. Maps `Healthy` and `Degraded` to 200 and `Unhealthy` to 503 by default.
5. Disables response caching unless explicitly allowed.
6. Writes the response and terminates the matching pipeline branch.

`MapHealthChecks` selects every registration. `MapReadinessCheck` selects the `ready` tag, and
`MapLivenessCheck` selects the `live` tag.

## Response serialization and AOT

The default `HealthCheckJsonResponseWriter` uses `Utf8JsonWriter`, not reflection-based
serialization. Diagnostic data is handled through a closed primitive-value switch with a string
fallback. Custom behavior is available through `IHealthResponseWriter`.

The registration, evaluation, and default response path remains trim- and NativeAOT-compatible.
The optional contributor adapter has its own hosting-family package and tests.

## Exception detail

Health endpoints are typically unauthenticated: orchestrators, load balancers and anyone who can
reach the listener probe them. A dependency's exception message routinely names hosts, databases,
users and ports, so publishing it is information exposure (CWE-209). In 10.0.0-preview.1 the
default writer published it twice: as the entry's `exception` member, and as its `description`,
which the service set to the same message when a check threw (#1409).

**What the default writer publishes.** The report's `status` and `totalDurationMs`, and per entry
`status`, `durationMs`, `description`, `tags` and `data`. It writes no exception text:

- It never writes the entry's `Exception`.
- A thrown or timed-out check carries the service's fixed description (above).
- A description the check returned is written as returned, unless it contains the message of the
  result's exception or of any exception in that exception's `InnerException` chain. Then the
  writer writes `The check reported an exception.` instead. That covers the common
  `HealthCheckResult.Unhealthy(exception.Message, exception)` idiom and a message formatted into
  a longer description. A description that paraphrases the exception is the check author's text
  and is published as returned.
- For a `data` value that is an `Exception`, the writer writes `The check reported an exception.`
  rather than the `ToString()` fallback it uses for other values outside the closed primitive set,
  because an exception's `ToString()` carries its type, message, inner exceptions and stack trace.
  Strings and other values in `data` are the check author's text and are published as returned,
  so a check must not put connection detail there.

**The opt-in.** `HealthEndpointOptions.IncludeDeveloperDetails`, `false` by default, is meant for a
development environment. It selects the writer's developer instance, which exposes exactly three
more things per entry:

- the description exactly as the entry carries it, including one that repeats an exception message;
- an `exception` member holding the `Message` of the entry's `Exception`: the exception the check
  threw, the `OperationCanceledException` of a timed-out check, or the exception the check
  returned in its result;
- the `Message` of an exception placed in `data`.

It never writes an exception's type, stack trace, inner exceptions or `Data`. The option governs
the default writer only; a custom `ResponseWriter` ignores it, as `Web.ErrorHandling`'s
`IncludeDeveloperDetails` governs only that boundary's own fallback. The endpoint resolves its
writer once, when the pipeline is composed.

**Breaking changes from 10.0.0-preview.1.** Opting in restores the `exception` member and returned
descriptions verbatim. It does not restore the preview.1 output:

- A thrown check's `description` is `The check threw an exception.` under either setting;
  preview.1 wrote the exception's message there. A client that parsed `description` for the
  failure reason now reads the fixed text, and finds the message only in `exception`, with the
  option on.
- A timed-out check's `description` is `The check timed out.` under either setting; preview.1
  wrote `The health check '<name>' timed out after <timeout>.`, so the timeout value is no longer
  published. With the option on, a timed-out check also gets an `exception` member holding the
  `OperationCanceledException`'s message; preview.1 wrote none.
- With the option off, the `exception` member is gone, and a returned description that contains
  an exception message is replaced by `The check reported an exception.`
- An `Exception` in `data` is written as that fixed text, or as its message with the option on;
  preview.1 wrote its `ToString()`.

**Server-side detail.** The exception object stays on the in-process report. An application reads
it through `IHttpHealthFeature.Report` on the exchange, from `IHealthCheckService.CheckHealthAsync`
directly, or in its own `IHealthResponseWriter`, which receives the whole report and decides what
to publish. The internal `HealthCheckEventSource` reports status transitions and failed probes
with their exception type and message (see Diagnostics).

## Framework packaging

Framework membership is current, not deferred:

- `App.Web` includes `Assimalign.Cohesion.Web.Health` as a public
  `CohesionFrameworkAssembly`, exposing it to Web application consumers.
- `App.Database` includes it as a private `CohesionFrameworkPrivateAssembly` alongside its private
  Web runtime closure. That inclusion supports Database runtime implementation without exposing
  Web health types through the Database reference surface.

A resource may still reference Web.Health privately when it needs the implementation internally,
but that is not the only consumption model and does not change App.Web's public API.

## Diagnostics

The internal `HealthCheckEventSource` is named `Assimalign.Cohesion.Web.Health`. It also reports
`Hosting.Health` contributors adapted by Web.Hosting; the adapter raises no second report.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | HealthCheckWorsened | Warning | checkName, previousStatus, status, cause, exceptionType, exceptionMessage |
| 2 | HealthCheckImproved | Informational | checkName, previousStatus, status |
| 3 | HealthCheckFailed | Verbose | checkName, failureStatus, cause, exceptionType, exceptionMessage, timeoutMilliseconds |

Transitions are tracked **per health-service instance**. Every `HealthChecksBuilder.Build()` creates
independent per-registration slots, initially Unknown (-1). A first Healthy observation is silent;
a first Degraded or Unhealthy observation worsens from Unknown. Each completed probe atomically
exchanges its slot, so concurrent readiness and liveness probes reporting the same state produce
one transition. Probes with different results transition in slot-exchange order; synchronous listener
callbacks can be delivered in a different order. Predicate-skipped checks and caller cancellation
leave their slots unchanged. Registering one check in two services produces independent transitions.

`cause` is Reported for returned results, Exception for thrown checks and Timeout for check
cancellation while the caller is still active. Worsening includes returned Unhealthy results even
without an exception. Repeated identical statuses produce no transition. Transitions are not
throttled; `HealthCheckFailed` is the Verbose per-probe detail for the two caught failure paths.
The lazy `failed-checks` PollingCounter counts those paths exactly, including when no listener is
enabled. Returned Unhealthy results and caller cancellation do not increment it.

Health exceptions carry their full type name and Message, never a stack trace. Dependency exception
messages can carry host, database or user names: the event-source rule explicitly retains these
application messages for diagnosis in server-side sinks. No event payload is added to the HTTP
response; the default response sanitization described above still applies.

**Behavior change from 10.0.0-preview.1 (#1406).** Listeners and configured event-source forwarding
sinks can now receive health transitions and exception detail that previously had no server-side
event. This is an observable diagnostics change on decision 15's terms, with no public API change.

## Non-goals

- **A DI-specific registration surface.** Composition remains explicit and container-neutral.
- **Runtime registry mutation.** The built service is an immutable snapshot.
- **Request-time service location.** Endpoint middleware receives the service directly.
- **An in-process health publisher.** This package evaluates and serves health; it does not publish
  control-plane state.
- **Reflection-based response serialization.** The default response path is statically authored.

## Exception messages (#1467)

Exception text lives in `src/Properties/ErrorMessages.resx`. The shared build targets generate
an internal `ErrorMessages` accessor in this project's root namespace under the intermediate
output directory; no designer is checked in. Formatted messages use
`string.Format(CultureInfo.CurrentCulture, ErrorMessages.<Name>, ...)`. Resource extraction
preserves the existing exception text and input-disclosure rules, including any diagnostic
type-only exclusions. EventSource templates, log messages and response bodies retain their
existing ownership. This is a conventions change with no public API or behavior break against
10.0.0-preview.1.
