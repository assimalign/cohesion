# ISchedulerApplicationBuilder

Namespace: `Assimalign.Cohesion.Scheduler`
Assembly: `Assimalign.Cohesion.Scheduler`

## Purpose

`ISchedulerApplicationBuilder` is the public composition seam for a Scheduler application. It extends `IHostBuilder` while refining `Build()` to return `ISchedulerApplication`.

## Surface and behavior

- `Build()` creates a configured Scheduler application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.Scheduler.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Hosting;

ISchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder(args);
await using ISchedulerApplication application = builder.Build();
```
