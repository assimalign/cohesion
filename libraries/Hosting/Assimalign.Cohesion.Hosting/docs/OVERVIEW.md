# Assimalign.Cohesion.Hosting

## Summary

Defines Cohesion's plain host abstraction, lifecycle coordinator, host context and environment,
service contracts, execution base classes, and the complete run.

## Package boundary

- Direct Cohesion dependency: `Assimalign.Cohesion.Core`.
- No resource context, control-plane, stdout protocol, mount, or process-exit ownership. The
  process's shutdown signals are handled only while `RunAsync` runs.
- No health contributor, health status, or health-report ownership.
- No dependency-injection, configuration, logging, or HTTP dependency.
- NativeAOT-compatible and reflection-free.

## Primary responsibilities

- `Host<TContext>` starts, stops, and tracks hosted services.
- `HostContext` and `IHostEnvironment` carry host-local runtime state.
- `BackgroundService` hosts asynchronous work on the thread pool.
- `DedicatedThreadService` hosts synchronous blocking work on a dedicated background thread.
- `IHost.Run()` / `IHost.RunAsync()` execute a complete lifetime: start, wait for a shutdown
  request or signal, and drain.
- `IHost.AsService()` adapts any host into an `IHostService` for nested composition; a nested host
  handles no signals.
- `IHostContext.WaitForShutdownAsync()` observes a shutdown request or terminal transition without
  polling.

## The complete run

`RunAsync` starts the host, waits until shutdown is requested, and drains it within
`ShutdownTimeout`. Re-entering the host while a run is active is rejected.

While the run is active, the process's shutdown signals (SIGINT, SIGQUIT and SIGTERM) request a
graceful shutdown instead of ending the process, exactly as cancelling the run's token does: before
the host has started, a signal cancels the start; afterward it drains the host. A host started with
`StartAsync`, such as one nested through `AsService()`, registers no signals.

When `StopAsync` begins outside the parked run, it wakes `RunAsync` and both callers join the same
stop-completion signal. The run cannot launch a second stop, and it rethrows the same stop failure.
The stop-begun marker belongs to the run, so a later run starts with fresh semantics.

## Key types

- `IHost`, `IHostBuilder`, `IHostContext`, `IHostEnvironment`
- `IHostService`, `IHostLifecycleService`
- `Host<TContext>`, `HostContext`, `HostEnvironment`, `HostOptions<TContext>`
- `BackgroundService`, `DedicatedThreadService`
- `HostState`, `HostException`, `HostStartupException`

## Hosting family

| Package | Owns |
| --- | --- |
| `Assimalign.Cohesion.Hosting` | Plain host lifetime and service execution |
| `Assimalign.Cohesion.ApplicationModel.Hosting` | Resource instances, their control plane, and the host service that joins one to its gateway (`libraries/ApplicationModel/`; builds on this package) |

The base `Assimalign.Cohesion.App` framework delivers both. Its presence does not activate resource
behavior: an application becomes a managed resource only when it calls its area's
`builder.Services.AddResource(...)`.
