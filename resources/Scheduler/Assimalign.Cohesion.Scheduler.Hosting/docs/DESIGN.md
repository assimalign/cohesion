# Scheduler Hosting Design

The public API is limited to the static `SchedulerApplication` factory. Builder, host, context, and options implementations remain internal, while their contracts live in `Assimalign.Cohesion.Scheduler`.

The application host delegates lifecycle execution to the shared Cohesion host. Routing through the planned ambient `ResourceRuntime` seam remains tracked in the source TODO for design item 12.

Scheduler domain execution is deliberately outside this filler host. The context currently exposes an empty hosted-service collection and a production host environment.
