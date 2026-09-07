# Scheduler Hosting Overview

`Assimalign.Cohesion.Scheduler.Hosting` supplies the concrete entry point for creating an `ISchedulerApplication`.

Call `SchedulerApplication.CreateBuilder(args)`, build the application, and run it with a cancellation token. The current filler host intentionally starts no hosted services; it exists to establish the resource-specific application boundary and lifecycle behavior.
