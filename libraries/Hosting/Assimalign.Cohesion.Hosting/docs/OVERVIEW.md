# Assimalign.Cohesion.Hosting

## Summary

Defines Cohesion's plain host abstraction, lifecycle coordinator, host context and environment,
service contracts, execution base classes, and public complete-run pipeline.

## Package boundary

- Direct Cohesion dependency: `Assimalign.Cohesion.Core`.
- No resource context, control-plane, signal, stdout protocol, mount, or process-exit ownership.
- No health contributor, health status, or health-report ownership.
- No dependency-injection, configuration, logging, or HTTP dependency.
- NativeAOT-compatible and reflection-free.

## Primary responsibilities

- `Host<TContext>` starts, stops, and tracks hosted services.
- `HostContext` and `IHostEnvironment` carry host-local runtime state.
- `BackgroundService` hosts asynchronous work on the thread pool.
- `DedicatedThreadService` hosts synchronous blocking work on a dedicated background thread.
- `IHostRunner`, `IHostRun`, and `IHostRunObserver` expose one complete host lifetime without
  coupling the host to a transport or supervisor.
- `IHost.Run()` / `IHost.RunAsync()` execute a complete lifetime through the configured runner.
- `IHost.AsService()` adapts any host into an `IHostService` for nested composition without
  executing the nested host's runner.
- `IHostContext.WaitForShutdownAsync()` observes a shutdown request or terminal transition without
  polling.

## Public run seam

`HostContext.Runner` is captured when `IHost.RunAsync` begins. The runner receives a fresh,
one-shot `IHostRun`; it may configure `ShutdownTimeout` and then delegates to
`IHostRun.RunAsync(observer, token)`. A runner that never invokes the handle never starts the host.
Re-entering the host while that run is active is rejected.

For a run whose started hook completes before a stop is accepted, observer transitions are
serialized and ordered:

`Started` → `Stopping` → (`DrainAborted` when the drain token is cancelled) → `Stopped`

The host enters `Started` before awaiting `OnStartedAsync`. If a stop is accepted during that hook,
the observer does not receive a late `Started`; the observed sequence begins with `Stopping` and
continues through the completed stop.

`TryShutdown` succeeds only for the current run while the host is exactly `Started`. It returns
false before startup, after the stop transition begins, after stop, and for a stale run handle.
The optional acceptance callback runs while the state lock still observes `Started`.

When `StopAsync` begins outside the parked run, it wakes `RunAsync` and both callers join the same
stop-completion signal. The run cannot launch a second stop, and it rethrows the same stop failure.
The stop-begun marker belongs to the run handle, so a later run starts with fresh semantics.

## Key types

- `IHost`, `IHostBuilder`, `IHostContext`, `IHostEnvironment`
- `IHostService`, `IHostLifecycleService`
- `IHostRunner`, `IHostRun`, `IHostRunObserver`
- `Host<TContext>`, `HostContext`, `HostEnvironment`, `HostOptions<TContext>`
- `BackgroundService`, `DedicatedThreadService`
- `HostState`, `HostException`, `HostStartupException`

## Hosting family

| Package | Owns |
| --- | --- |
| `Assimalign.Cohesion.Hosting` | Plain host lifetime and service execution |
| `Assimalign.Cohesion.Hosting.Health` | Health vocabulary shared by resource and delivery packages |
| `Assimalign.Cohesion.Hosting.Resources` | Opt-in resource runtime and supervisor-facing process behavior |

Consumers can reference Health without Hosting, while Resources composes both siblings explicitly.
The base `Assimalign.Cohesion.App` framework nevertheless delivers Resources and Health beside
Hosting because opt-in SDK-generated code targets `Hosting.Resources.ResourceRuntime` and Health is
in Resources' dependency closure. Their presence does not activate resource behavior;
`CohesionApplicationModel=enabled` does.
