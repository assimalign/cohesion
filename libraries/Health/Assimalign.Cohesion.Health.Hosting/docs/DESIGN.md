# Assimalign.Cohesion.Health.Hosting — Design

## Design intent

The hosting seam that bridges the dependency-free `Assimalign.Cohesion.Health` core to a
running host. It owns two things and nothing else:

1. **The periodic publisher** — an `IHostService` that evaluates the registered checks on an
   interval and hands each `HealthReport` to every registered `IHealthPublisher`.
2. **The DI extensions** — `AddHealthChecks`, `AddHealthCheckPublisher` — the builder-time
   composition surface.

This is the only place health touches dependency injection, per the repo-wide rule that
`*.Hosting` is the sole DI/Logging/Config seam and core libraries stay DI-free.

## Why a `BackgroundService` on the Hosting execution menu

`libraries/Hosting` exposes an execution menu — `BackgroundService` (pool-scheduled async
loops), `DedicatedThreadService` (blocking loops that own a thread), and raw `IHostService`.
The publisher is a **`BackgroundService`**: its loop is asynchronous I/O paced by a
`PeriodicTimer`, so it must not own an OS thread for its whole life. It is discovered and
driven by the host exactly like any other host service — `AddHealthChecks` registers it as
`IHostService`, and the host's `HostedServices` enumerable starts and stops it with the
application.

## The publish loop

```
if no publishers registered: return   // nothing consumes reports — don't waste probe cycles
await Task.Delay(Delay)                // startup offset so the first eval doesn't race startup
using timer = new PeriodicTimer(Period)
do
    PublishCycle()                     // evaluate + fan-out
while (await timer.WaitForNextTickAsync())
```

`PublishCycle`:

1. Evaluate `IHealthCheckService.CheckHealthAsync(Predicate)` under a per-cycle `Timeout`
   (linked CTS off the host token). A cycle that exceeds its budget is **skipped**, not
   fatal — the next tick retries.
2. Fan the report out to every publisher. A publisher that **throws is swallowed** so it
   neither kills the loop nor starves its siblings; the report still reaches the others and
   the next cycle retries the failing one. Only host cancellation ends the loop.

The "no publishers ⇒ return immediately" short-circuit means `AddHealthChecks` can register
the host service unconditionally: with nothing to publish to, the loop is a no-op rather than
a wasted probe every `Period`.

## DI composition

`AddHealthChecks(configurePublisher)`:

- Creates an `IHealthChecksBuilder` (container-free) and returns it for `AddCheck` chaining.
- Registers `IHealthCheckService` as a factory that calls `builder.Build()` on first resolve
  — capturing every `AddCheck` made during host build (all builder-time).
- Registers the single `HealthCheckPublisherOptions` instance (already configured via the
  callback) and the publisher `IHostService`.

`AddHealthCheckPublisher(instance)` / `AddHealthCheckPublisher<T>()` register publishers. The
publisher host service resolves them via `GetServices<IHealthPublisher>()`, which yields an
empty set when none are registered (hence the loop's short-circuit).

Call `AddHealthChecks` **once** and chain `AddCheck` on the returned builder. The options are
supplied inline through the callback rather than a separate `Configure` step, so there is no
mutable-singleton-after-build hazard.

## Options

`HealthCheckPublisherOptions` validates on set:

| Option | Default | Meaning |
|--------|---------|---------|
| `Delay` | 5s | Startup offset before the first cycle (must be ≥ 0). |
| `Period` | 30s | Interval between cycles (must be > 0 — a `PeriodicTimer` requires it). |
| `Timeout` | 30s | Per-cycle evaluation budget (> 0 or `Timeout.InfiniteTimeSpan`). |
| `Predicate` | `null` | Which checks each cycle runs; `null` ⇒ all. |

## AOT posture

The publisher and options are reflection-free. The project references
`Assimalign.Cohesion.DependencyInjection`, whose container emits trim/AOT analyzer warnings
from its expression/IL fallbacks; those originate in the shared container, not in this code,
and are the reason DI wiring is confined to this `.Hosting` seam rather than the core.

## Non-goals

- **Owning the check model.** Contracts and the engine live in the core; this project only
  drives them on a schedule and wires DI.
- **Concurrent publish fan-out.** Publishers are notified sequentially so a slow publisher's
  failure isolation is simple and ordering is deterministic.
- **Reverse dependency on ApplicationModel.** The orchestration bridge is a publisher a
  consumer registers, not a reference this project holds.
