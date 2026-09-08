# ISchedulerApplication

Namespace: `Assimalign.Cohesion.Scheduler`
Assembly: `Assimalign.Cohesion.Scheduler`

## Purpose

`ISchedulerApplication` is the public lifecycle contract for a Scheduler application. It extends `IHost` and adds the run-until-shutdown operation used by executable resources.

## Surface

- `RunAsync(CancellationToken cancellationToken = default)` starts the host, waits for shutdown, and completes after the host has stopped.
- The inherited `IHost` members expose the host identity, context, start, stop, and disposal lifecycle.

The application runs the host services registered on its builder, or an empty collection when none were supplied. Services start in registration order and stop in reverse. A token that is already cancelled still drives the host through a clean start-and-stop transition; cancellation after startup requests graceful shutdown.

## Exceptions

`RunAsync` throws `ObjectDisposedException` when invoked after the application has been disposed. Lifecycle failures are propagated to the caller.

## Usage

```csharp
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Hosting;

await using ISchedulerApplication application =
    SchedulerApplication.CreateBuilder(args).Build();

await application.RunAsync(cancellationToken);
```
