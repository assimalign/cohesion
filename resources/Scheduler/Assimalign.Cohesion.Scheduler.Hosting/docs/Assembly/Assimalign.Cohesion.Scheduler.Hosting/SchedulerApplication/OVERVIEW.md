# SchedulerApplication

Namespace: `Assimalign.Cohesion.Scheduler.Hosting`
Assembly: `Assimalign.Cohesion.Scheduler.Hosting`

## Purpose

`SchedulerApplication` is the public factory for the Scheduler hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `ISchedulerApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder materializes its registered service factories once against the new Scheduler context and preserves registration order; no services are added by default.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Hosting;

ISchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder(args);
await using ISchedulerApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
