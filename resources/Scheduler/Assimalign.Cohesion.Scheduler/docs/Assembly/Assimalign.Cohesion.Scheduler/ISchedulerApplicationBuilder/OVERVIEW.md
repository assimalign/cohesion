# ISchedulerApplicationBuilder

Namespace: `Assimalign.Cohesion.Scheduler`
Assembly: `Assimalign.Cohesion.Scheduler`

## Purpose

`ISchedulerApplicationBuilder` is the public composition seam for a Scheduler application. It extends `IHostBuilder` while refining `Build()` to return `ISchedulerApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new Scheduler context.
- `Build()` creates a configured Scheduler application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.Scheduler.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Hosting;

ISchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder(args);
await using ISchedulerApplication application = builder.Build();
```
