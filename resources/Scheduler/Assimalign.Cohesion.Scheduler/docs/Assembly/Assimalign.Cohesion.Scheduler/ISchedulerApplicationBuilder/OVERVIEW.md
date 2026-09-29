# ISchedulerApplicationBuilder

Namespace: Assimalign.Cohesion.Scheduler

Assembly: Assimalign.Cohesion.Scheduler

The builder separates job declaration from trigger binding:

- AddJob registers an IScheduleJob without executing it.
- AddScheduleProvider contributes schedules for Hosting to run.
- Build returns the typed ISchedulerApplication.

Feature packages expose convenient AddCronSchedule and AddTimerSchedule verbs over AddScheduleProvider. Hosting rejects providers whose schedules bind a job that was not declared through AddJob.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `ISchedulerApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ISchedulerApplicationContext` exposes `ContentRootPath` plus the immutable `Jobs` and `ScheduleProviders` snapshots. `ISchedulerApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`SchedulerApplication.CreateBuilder(args)` returns the public concrete `SchedulerApplicationBuilder`; its `Build()` returns the public `SchedulerApplication : Host<SchedulerApplicationContext>`. The public `SchedulerApplicationContext` implements `ISchedulerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
