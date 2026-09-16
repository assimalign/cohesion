# Scheduler Hosting Overview

Assimalign.Cohesion.Scheduler.Hosting supplies SchedulerApplication.CreateBuilder. The concrete application executes all schedules supplied through registered providers while preserving caller-added host services.

Enabled resources inherit their environment, content root, http endpoint, bootstrap credential, and shutdown grace from ResourceRuntime. The runtime observes the endpoint, attaches the default control plane, exposes health/readiness/liveness and management routes, and drains active occurrences during stop.

## Concrete composition (T10 / O34)

`SchedulerApplication.CreateBuilder(args)` returns the public concrete `SchedulerApplicationBuilder`; its `Build()` returns the public `SchedulerApplication : Host<SchedulerApplicationContext>`. The public `SchedulerApplicationContext` implements `ISchedulerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `SchedulerApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<SchedulerApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
