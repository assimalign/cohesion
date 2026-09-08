# Scheduler Hosting Overview

`Assimalign.Cohesion.Scheduler.Hosting` supplies the concrete entry point for creating an `ISchedulerApplication`.

Call `SchedulerApplication.CreateBuilder(args)`, register host-service instances or context-aware factories, build the application, and run it with a cancellation token. Each factory is materialized once per build against the new scheduler context; services start in registration order and stop in reverse. The composition-only host registers no scheduler services by default.
