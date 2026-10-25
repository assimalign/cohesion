# Assimalign.Cohesion.Hosting Design

## Design intent

The plain Hosting package coordinates a host lifetime without assuming how an application is
supervised, how health is transported, or whether the process represents a Cohesion resource.
Those policies live in sibling packages and integrate through the public run seam.

## Lifecycle coordinator

A normal start/stop cycle runs these phases:

`OnStartingAsync` → services `StartingAsync`/`StartAsync`/`StartedAsync` → `OnStartedAsync` →
running → `OnStoppingAsync` → services `StoppingAsync`/`StopAsync`/`StoppedAsync` →
`OnStoppedAsync`

Startup is dependency-sensitive: serial startup stops at the first failure, while configured
concurrent startup collects failures. Either path performs best-effort reverse-order compensation,
marks the host `Failed`, completes its run signals, and preserves the original startup exception.
This finalization also runs when startup budget setup or rollback service resolution/enumeration
throws. A retry executes a fresh attempt and surfaces a persistent failure again. Compared with
10.0.0-preview.1, such retries now throw instead of silently succeeding while stuck in `Starting`
(#1079; behavioral breaking change).

On failure, the coordinator disarms the startup timer and drains its shutdown registration before
compensation can dispose run state or clear the shutdown callback. Reporting therefore cannot
leave a startup timer targeting a rolled-back host, even when the hook outlasts the startup deadline.

After compensation and run-state reset, the coordinator invokes
`OnStartFailedAsync(Exception exception, CancellationToken cancellationToken = default)` exactly
once for each accepted startup that fails, except caller cancellation. A start is accepted when
`OnStartingAsync` completes successfully; a rejection from that hook has performed no service
startup and is not reported. An already-cancelled caller token is rejected before initialization
or `OnStartingAsync`, without consuming a derived host's lifecycle. Cancellation during accepted
startup still rolls back, but an `OperationCanceledException` with the caller token cancelled is
not reported. The same applies to a non-empty `AggregateException` whose flattened inner exceptions
are all `OperationCanceledException` instances (including `TaskCanceledException`). Empty aggregates,
mixed aggregates containing a genuine failure, and cancellations without a cancelled caller token
still report. Cancellation provenance is captured before rollback. A genuine non-cancellation
failure is still reported if caller cancellation races it; the original aggregate is propagated unchanged.
This protected virtual hook defaults to a completed task;
derived hosts may report the failure without reimplementing startup or rollback. The supplied
token is independent of startup and is signaled on reporting-budget expiry. The hook
receives the exception that the caller will observe, including the existing `HostStartupException`
translation when the readiness budget expires. Ordinary failures retain their identity and stack.
A successful start, repeated start while running, lifecycle rejection, later stop, or disposal
does not call it. Hosts that permit retries keep that behavior; each accepted failed retry reports once.

Reporting has a fixed **one-second budget**, starting when the invocation is queued after rollback.
Invocation runs on a dedicated background thread (`LongRunning` on the default task scheduler,
then `Unwrap`), so an override that blocks before returning its task cannot occupy the pool worker
needed to expire its own budget, even when the pool has only one worker. The returned asynchronous
task remains bounded and observed; its continuations use their normal scheduling. This costs one
thread per reported failed start, including hosts using the default no-op hook. No override detection
or reflection is used. Skipping the default hook would require explicit opt-in through another
protected contract or constructor registration; that extra surface is not justified for this failure
path. A synchronously blocked override retains its background thread until it returns.
The private `_startFailureReportingTimeout` constant deliberately does not reuse
`ShutdownTimeout`: service drain can be infinite, while diagnostics must never indefinitely withhold
the startup failure. A completed report is awaited; expiry propagates the original startup failure
and requests cancellation asynchronously, so even blocking or throwing cancellation callbacks cannot
hold the caller. This bounds reporting only, not service compensation.

A hook failure or reporting-budget `TimeoutException` does not replace the startup exception. It is attached as the exception object in
`exception.Data["Assimalign.Cohesion.Hosting.StartFailureHookException"]`. Attachment is best-effort:
if a custom exception's `Data` implementation rejects it, the original startup failure still
propagates. The host always retains the hook failure in its private `_startFailureHookException`
field for debugger inspection, including when attachment is unavailable, and clears that record
when the next start is accepted. This avoids introducing a second public failure result or coupling
the general host to one application's diagnostics transport.

An abandoned hook cannot be forcibly stopped. Its task and any asynchronous cancellation callback
faults are observed independently when they settle, even if the other task never completes;
the cancellation source is disposed after both settle. A late
fault does not mutate the exception already returned or the diagnostic record of a later start.
Overrides should cooperate with cancellation and avoid touching resources that the caller may
dispose after `StartAsync` returns. Reporting is best-effort; the original failure remains authoritative.

Known cleanup gap: `Host<TContext>.RollbackStartAsync` suppresses exceptions from compensating
service `StopAsync` calls without recording them. Preserving the startup failure is intentional,
but retaining those cleanup failures requires threading the primary failure through rollback and
handling several secondary failures; it is not a one-line, behavior-preserving change. This remains
a follow-up, separate from the startup-failure reporting hook.

Graceful shutdown is best-effort. Every service receives a stop attempt, in reverse registration
order or concurrently according to `StopServicesConcurrently`, and failures are reported after the
host reaches `Stopped`. Stopping an `Idle`, already stopping, `Stopped`, or compensated `Failed`
host is a no-op.

Per-run cancellation signals that shutdown should begin; it is not the graceful-drain budget.
`StopAsync` creates a fresh linked token and applies `ShutdownTimeout`, preventing the already
cancelled run token from pre-cancelling every service drain. Coordinator-owned reset makes a cleanly
stopped or compensated host restartable without relying on a derived hook calling `base`.

A run token already cancelled at entry performs one complete lifecycle: startup uses
`CancellationToken.None`, followed immediately by graceful shutdown with a fresh stop budget.
The run completes in `Stopped` and delivers the normal observer sequence. A startup failure still
rolls back to `Failed` and propagates. Both the concrete host and the plain `IHost` extension own
this semantic; applications need no shadows. Stop completion is joined only if stopping actually
began, so a rejected stop cannot strand a run on a completion signal that will never fire.

`IHostContext.WaitForShutdownAsync` is the public lifecycle observation seam. It completes when
shutdown is requested or the current lifetime begins stopping, stops, or fails; cancelling one
wait abandons only that caller and does not signal the host. A later start creates a fresh signal.

## Complete-run pipeline

`HostContext.Runner` is an optional pipeline around `IHost.RunAsync`. The host captures the runner
once at the start of each run, creates a new `IHostRun`, and rejects concurrent re-entry even when a
runner delays before executing that handle. Replacing `HostContext.Runner` affects only later runs.

`IHostRun` is deliberately one-shot. It exposes:

- the `IHost` being run;
- a configurable `ShutdownTimeout` applied beginning with that run;
- `RunAsync`, which performs startup, waits for shutdown, drains, and joins completion; and
- `TryShutdown`, a race-safe request for the current run.

A runner can subscribe to platform signals, enforce policy, or translate failures without an
internal host decorator. Plain runs use the same handle with no observer, so the extension point
does not create a second lifecycle implementation.

## Nested host composition

`IHost.AsService()` returns an internal `HostToServiceWrapper(IHost)` exposed only as an
`IHostService`. Its `StartAsync` awaits the nested host's complete startup and returns only when the
child reports `Started`, so the parent cannot report readiness early. The parent's startup token
also bounds the await even for an external `IHost` implementation that does not itself observe the
token; expiry is surfaced by the parent as `HostStartupException`.

The wrapper retains the raw child startup operation for that nested lifetime. If the parent's
bounded readiness wait ends while an external child is still starting, parent rollback records a
deferred stop, makes an immediate best-effort stop request, and returns without awaiting the
unbounded child operation. When that operation eventually settles, the wrapper retries the stop for
a still-`Starting` or `Started` child, even when the outer token has already expired. A child that
starts after the outer budget therefore cannot escape as a running orphan, and the incomplete
cleanup prevents the same wrapper from beginning a new nested lifetime.

Stopping the wrapper passes the parent's still-live drain token to the child. The child's own
`ShutdownTimeout` is linked inside its `StopAsync`, making the effective budget the shorter of the
remaining parent budget and the child's budget. One stop task is retained per nested lifetime so
concurrent callers join the same child drain and observe the same failure. Serial parent shutdown
uses the host's normal reverse-registration traversal; strict reverse dependency order therefore
requires `StopServicesConcurrently` to remain disabled.

Shutdown-budget tests observe cancellation separately from completion: a child stop delegate can
report its cancelled token before the child reaches `Stopped`. State assertions therefore await
that state for both hosts under the test timeout, while the shorter parent budget still bounds the
child drain. This changes only test synchronization, with no runtime change from 10.0.0-preview.1.

Nesting is lifecycle composition, not process-policy composition. The wrapper calls only the
child's `StartAsync` and `StopAsync`; it never invokes, replaces, or clears the child's
`HostContext.Runner`. Only the outer host's direct `RunAsync` owns the process-level runner and its
supervisor protocol. Child startup and stop failures flow through the wrapper and fault the
parent's complete run.

### Observer contract

When `OnStartedAsync` completes before a stop is accepted, callbacks are delivered in this order:

1. `Started`, after startup and `OnStartedAsync` complete.
2. `Stopping`, after the host atomically accepts the transition to `Stopping` and before drain work.
3. `Stopped`, after services, state reset, and `OnStoppedAsync` complete.

The host enters `Started` before it awaits `OnStartedAsync`, allowing an external stop to be
accepted during that hook. In that race, the observer never receives a late `Started`; its sequence
begins with `Stopping` and continues through the completed stop.

If the graceful-drain token is cancelled, `DrainAborted` occurs at most once between `Stopping` and
`Stopped`. `Stopped` still reports the completed stop sequence. An observer callback is never
allowed to strand teardown: its first exception is retained, shutdown proceeds, and the run
surfaces it after coordinated stop unless a stop failure takes precedence. A `Started` observer
failure requests shutdown immediately.

### Stop join and per-run stop ownership

The active run owns a stop-completion signal and a stop-begun marker. When an external caller invokes
`IHost.StopAsync`, the state transition marks that run as stopping and wakes the parked run. The
run sees that stopping already began, does not call `StopAsync` again, waits for the same completion,
and observes the same stop exception. Disposal uses the same behavior rather than racing a second
teardown.

These fields belong to one `IHostRun`. After completion the host clears that handle; a subsequent
run receives a fresh marker and completion signal. This prevents stop state from leaking across
restarts.

### `TryShutdown` acceptance

`IHostRun.TryShutdown` returns true only when all three conditions hold atomically:

- the host state is exactly `Started`;
- the handle is still the host's current run; and
- the current run's shutdown callback is installed.

The optional `onAccepted` callback executes under the host state lock before cancellation is
signalled. Calls made before `Started`, once `Stopping` begins, after `Stopped`/`Failed`, or through
an old handle return false. This makes signal adapters race-safe and prevents a late signal from
shutting down a later run.

## Service execution menu

The host supplies lifecycle, not a global scheduler. Each service chooses its execution shape:

| Work | Type | Completion joined by `StopAsync` |
| --- | --- | --- |
| Asynchronous I/O loop | `BackgroundService` | The real task returned by `ExecuteAsync` |
| Synchronous blocking loop | `DedicatedThreadService` | The owned background OS thread |
| Component owning multiple loops or threads | Direct `IHostService` implementation | The component's explicit stop task |

Both base classes signal their work token and join the real work. Cooperative cancellation is a
clean stop; other faults surface to the host. A timed-out join retains run state so a later stop can
join the same work.

## Family map and dependency direction

| Package | Direct dependencies | Policy boundary |
| --- | --- | --- |
| `Assimalign.Cohesion.Hosting` | Core | Plain lifecycle only |
| `Assimalign.Cohesion.Hosting.Resources` | Core, Hosting, ProtectedData | Health contracts and aggregation, resource invocation, context, supervisor protocol, and process policy |

Hosting does not reference Resources. Resources owns health contracts and is the
opt-in composition layer and installs its runner through `HostContext.Runner`.

Resources ships in the base `Assimalign.Cohesion.App` framework. All opt-in resource SDKs
generate calls to `Hosting.Resources.ResourceRuntime`, so Resources belongs beside the shared
Hosting assembly. Framework presence is not activation: only an
enabled `CohesionApplicationModel` registration causes an area builder to install the runner.

## Non-goals

- Process signal handling, stdout readiness messages, exit-code classification, and resource
  invocation belong to `Assimalign.Cohesion.Hosting.Resources`.
- Health contributors and aggregated health values belong to
  `Assimalign.Cohesion.Hosting.Resources` and its matching namespace.
- Dependency injection, configuration, logging, and HTTP delivery belong to their respective
  packages.
- Hosting does not install a synchronization context or task scheduler.

## Environment names

Host environment predicates use ordinal case-insensitive matching. `IsLocal()` identifies the
developer-machine environment; `IsDevelopment()` identifies an ordinary deployable environment
and grants no developer-only fallback. Names come from `AppEnvironment.Keys`; plain-host and
Core unset defaults remain Production.

## Startup-hook rejection (Phase 29)

A failure in `OnStartingAsync` consumes no service lifecycle work. The host resets
its run signal and marks the attempt failed, but does not call service `StopAsync`
for an attempt that never reached service startup. This lets area hosts enforce
terminal lifecycle rules without stopping the prior run's services a second time.
Once the hook succeeds, existing rollback still stops services in reverse order
on lifecycle/startup failure. The generic host retains its supported restart behavior.
