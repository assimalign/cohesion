# Assimalign.Cohesion.Hosting Design

## Design Intent

The package splits host runtime concerns into explicit contracts: a host orchestrates lifecycle, a context carries state, and hosted services implement the start and stop behavior. That keeps runtime composition readable and testable.

## Architecture

- Host<TContext> is the lifecycle coordinator that starts, stops, and tracks hosted services. It is threading-neutral: starting and stopping are pure await state machines that spawn no threads and install no SynchronizationContext or TaskScheduler.
- HostContext and IHostEnvironment isolate runtime state from the host implementation itself.
- BackgroundService is the convenience base class for long-running units of *asynchronous* work inside a host. It is pool-scheduled: `StartAsync` launches `ExecuteAsync` directly and stores the real work task (no `Task.Factory.StartNew` wrapper, no `LongRunning`), so `StopAsync` cancels and then joins that exact task within the caller's shutdown budget, and any fault thrown by `ExecuteAsync` surfaces to the host (synchronous faults during start, post-yield faults on stop) instead of being swallowed. A cooperative cancellation exit is treated as a clean stop.
- DedicatedThreadService is the base class for *synchronous, blocking* units of work that own a dedicated background OS thread for their entire life.

## Lifecycle

A start/stop cycle drives four host-level specialization hooks around the service phases, in this order:

`OnStartingAsync` → services `StartingAsync`/`StartAsync`/`StartedAsync` → `OnStartedAsync` → *(running)* → `OnStoppingAsync` → services `StoppingAsync`/`StopAsync`/`StoppedAsync` → `OnStoppedAsync`

- `OnStartingAsync`/`OnStartedAsync` bracket startup; `OnStoppingAsync`/`OnStoppedAsync` bracket shutdown (e.g. a database engine's checkpoint or a web app's connection drain hangs off `OnStoppingAsync`). The stop-side hooks receive the shutdown-budget token (`ShutdownTimeout`).
- **Run-state reset is coordinator-owned.** After the host reaches `Stopped`, `StopAsync` itself resets the per-run state (run token source, run signal, shutdown callback, init flag). It is deliberately *not* the job of `OnStoppedAsync`: an overridable hook a subclass may forget to base-call must not own restart correctness. A cleanly stopped host can be started - or `RunAsync`-driven - again.
- **The run token is a signal, not a stop budget.** `RunAsync` parks on a run signal completed by shutdown (`Context.Shutdown()` cancels the run token) and then stops with a *fresh* token: the run token is cancelled by definition at that point, so using it would pre-cancel every service's graceful drain. The stop budget always comes from `ShutdownTimeout` inside `StopAsync`.
- A direct `StopAsync` (without a shutdown signal) also completes the run signal, so a parked `RunAsync` unwinds instead of hanging forever.
- A shutdown signal arriving after the host has stopped is a no-op, not a fault.

### Start failure

A failed or cancelled start never wedges the host in `Starting` with partially-started services leaked (e.g. a bound socket). The coordinator compensates and rethrows:

- Every hosted service is stopped **best-effort** in reverse registration order, on a fresh `ShutdownTimeout` budget, *without* the lifecycle stop ceremony (`StoppingAsync`/`StoppedAsync` and the host stop hooks do not fire - this is compensation, not a graceful stop). Rollback failures are swallowed so the original fault is what the caller observes.
- The host transitions to the terminal `HostState.Failed` - distinct from a clean `Stopped` - and the run/stopped signals complete, so a parked `RunAsync` or nested-host wrapper unwinds.
- A `Failed` host is not wedged: `StopAsync` is a clean no-op (teardown already happened), disposal works, and a retried `StartAsync` is allowed (the state machine treats `Failed` like `Stopped` for restart, so a supervisor can retry a transient failure).

## Execution model - the per-service menu

`Host<TContext>` imposes no execution model, so where a unit of work meets a thread is decided per service, by the service class that knows its own I/O profile, through static dispatch: pick a base class at authoring time. There is no host-level threading strategy and no reflection.

| Kind of work | Menu member | How you author it |
| --- | --- | --- |
| Async I/O loop (accept sockets, timer tick, queue polling) | `BackgroundService` (pool-scheduled) | override async `ExecuteAsync` |
| Blocking loop (synchronous file/device I/O, flush worker, tight CPU loop) | `DedicatedThreadService` (its own OS thread) | override synchronous `Run` |
| Component that owns N threads internally (e.g. per-core event loops) | implement `IHostService` directly | `StartAsync` spins up its loops; `StopAsync` joins them |

The crux distinction: `BackgroundService` gives you an **async** `ExecuteAsync` that cooperates with the thread pool via `await`; `DedicatedThreadService` gives you a **synchronous** `Run` that owns a dedicated background OS thread for its whole life (what `TaskCreationOptions.LongRunning` only pretended to give an async body).

Both bases share one lifecycle contract: `StartAsync` launches the work and returns; `StopAsync` signals the work's token then joins the real work within the caller's shutdown budget; a cooperative `OperationCanceledException` exit is a clean stop; any other fault the work throws is rethrown from `StopAsync` so the host observes it (`DedicatedThreadService` additionally marshals the fault off its thread, where escaping would terminate the process). A drain timeout keeps run-state so a retried stop can rejoin the same work, and both bases can be started again after a clean stop.

A single host composes whichever members its units of work need:

```csharp
// Database engine host - dedicated threads for blocking I/O, pooled for the async endpoint
//   WriteAheadFlushService : DedicatedThreadService   // its own blocking thread
//   PageWriterService      : DedicatedThreadService   // its own blocking thread
//   QueryEndpointService   : BackgroundService        // pooled async accept loop

// Scheduler host - one pooled timer loop, nothing dedicated
//   SchedulerTickService   : BackgroundService        // pooled: while (...) { await Task.Delay(...); Fire(); }
```

### Why `Run` is synchronous `void`, not `Task`

"Async work on a dedicated thread" is a contradiction, and the signature is what keeps this base honest:

- **A dedicated OS thread executes exactly one synchronous call frame** - start, run the delegate, exit. `void Run` maps one-to-one onto that life, so "the method returned" and "the work finished" are the same event. That identity is what lets the thread body marshal faults and complete the exit signal the stop path joins: thread exit *is* work completion.
- **A `Task`-returning `Run` would evaporate off the thread at its first `await`.** An async body runs on its starting thread only up to its first await; with no `SynchronizationContext` installed, every continuation after that is scheduled on the thread pool. The "dedicated" thread would idle or exit while the real work migrated to the pool - exactly the illusion `TaskCreationOptions.LongRunning` created inside the old `BackgroundService` (a dedicated thread for the synchronous prologue only), which this feature removed. An async signature here would rebuild that bug into the base whose reason to exist is the real thread.
- **Keeping genuinely async code pinned to one thread requires an event-loop substrate** - a single-threaded `SynchronizationContext`/`TaskScheduler` pumping continuations back onto that thread. That is a different and much larger component, and hiding a mini-pump inside this base would smuggle a scheduler into what is meant to be the dumbest, most predictable member of the menu. See Non-goals below for how the substrate case is modeled instead.
- **The `void` signature is the menu's enforcement mechanism.** You cannot `await` in it, so the vocabulary that is correct on an owned thread - blocking waits, synchronous I/O, sleep-paced loops - is the natural one, and the moment a body wants `await` the compiler pushes it to `BackgroundService`. With a `Task` signature that misuse would compile cleanly and silently run on the pool, turning the menu's crux distinction into a lie with no compiler, analyzer, or runtime signal.
- **A service that genuinely needs both shapes composes rather than merges**: register one `DedicatedThreadService` and one `BackgroundService` (the database-engine example above), or implement `IHostService` directly and own the threads (menu member three).

This mirrors `ThreadStart` itself being void: .NET has never shipped an "async on a dedicated thread" primitive, because the two do not compose without a pump.

### Escape hatch (reserved, not shipped): `IServiceExecutor`

The menu is static dispatch by design. The only case that justifies an injected launch seam is a service body that must be launched *differently by configuration* - the same loop pooled in one deployment, dedicated in another. That seam is reserved with this shape:

```csharp
public interface IServiceExecutor
{
    // Returns the REAL work task the host joins on shutdown.
    Task Execute(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}
```

It is intentionally not shipped until a configuration-varying launch actually exists; do not introduce it for cases the menu already covers.

### Nesting hosts

`Host.AsService()` adapts a host into an `IHostService` so one host can run inside another (the splithost / multiservice composition). The wrapper starts the wrapped host, then parks - without polling - on the context's stopped signal (`HostContext.WhenStoppedAsync()`, completed on the transition to `HostState.Stopped`, reset on a later start), so an idle nested host consumes no CPU. When the outer host stops the service, the wrapper stops the wrapped host with a fresh token so it receives its own shutdown budget (the outer token is already cancelled on that path); when the wrapped host stops on its own, the wrapper's work completes so the outer host's accounting reflects it. The signal is keyed to the explicit `Stopped` transition, never to the `HostState.Running`/`Started` alias.

### Non-goals

- No threading knob or strategy on `Host<TContext>`. The host imposes no execution model; the per-service bases above are the seam.
- No host-owned execution substrate (process-wide `SynchronizationContext` / `TaskScheduler`). The one genuine substrate case - a thread-per-core server whose sibling services must resume on per-core event loops - should be modeled as a substrate service registered first (serial registration-order start installs it before siblings), not as a `Host<>` strategy.

## Layout Example

```text
Assimalign.Cohesion.Hosting/
  src/
    Assimalign.Cohesion.Hosting.csproj
    Abstractions/
    Exceptions/
    Extensions/
    Implementation/
    Internal/
    Properties/
    ValueObjects/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Implement a long-running hosted service

```csharp
internal sealed class Worker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

## Example 2: Implement a blocking hosted service on a dedicated thread

```csharp
internal sealed class FlushWorker : DedicatedThreadService
{
    protected override void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Synchronous flush work.
        }
    }
}
```

## Example 3: Run a host or expose it as a service

```csharp
Host<MyHostContext> host = CreateHost();

await host.RunAsync(cancellationToken);
IHostService service = host.AsService();
```
