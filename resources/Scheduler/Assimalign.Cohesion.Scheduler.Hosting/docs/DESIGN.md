# Scheduler Hosting Design

The public API is limited to the static `SchedulerApplication` factory. Builder, host, context, and options implementations remain internal, while their contracts live in `Assimalign.Cohesion.Scheduler`.

The application host delegates lifecycle execution to the shared Cohesion host. Routing through the planned ambient `ResourceRuntime` seam remains tracked in the source TODO for design item 12.

Each build creates a new context, invokes every registered service factory exactly once against that context, and exposes the materialized services as an ordered, read-only hosted-service snapshot. The shared host starts services in registration order and stops them in reverse; an unconfigured builder still produces an empty collection and a production host environment.

Scheduler domain execution remains deliberately outside this composition-only host, and no scheduler service is registered by default.
