# Assimalign.Cohesion.Scheduler

The root package defines Scheduler application, context, job, schedule, and provider contracts. SchedulerApplication.CreateBuilder lives in the Hosting package.

Declare delegate-backed work with AddJob. The returned IScheduleJob remains dormant until AddCronSchedule or AddTimerSchedule binds it. Build validates that every scheduled job is the same instance present in the declaration registry.

ISchedulerApplicationContext exposes read-only job and provider snapshots for observation and hosting. ISchedule exposes next and last scheduled timestamps, status, priority, retry metadata, bound jobs, and its asynchronous evaluation loop.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `ISchedulerApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ISchedulerApplicationContext` exposes `ContentRootPath` plus the immutable `Jobs` and `ScheduleProviders` snapshots. `ISchedulerApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`SchedulerApplication.CreateBuilder(args)` returns the public concrete `SchedulerApplicationBuilder`; its `Build()` returns the public `SchedulerApplication : Host<SchedulerApplicationContext>`. The public `SchedulerApplicationContext` implements `ISchedulerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
