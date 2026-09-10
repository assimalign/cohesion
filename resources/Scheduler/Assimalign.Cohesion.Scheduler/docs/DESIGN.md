# Assimalign.Cohesion.Scheduler Design

## Contract seam

ISchedulerApplicationBuilder owns two independent registries: declared jobs and schedule providers. AddJob only declares work and is intentionally dormant. Cron and Timer builder verbs create their internal IScheduleProvider implementations and bind an already-declared job. Hosting validates those bindings during Build so a schedule cannot silently capture an undeclared job instance.

ISchedulerApplicationContext exposes immutable job and provider snapshots. Hosting depends on those root contracts and never needs a feature-package reference.

## Occurrences and shutdown

Schedule<TContext> owns schedule identity, observable status, last and next occurrence timestamps, per-provider enablement checks, and sequential execution of the jobs bound to one occurrence. A provider cancellation token stops future waits and prevents another occurrence. Once a job begins, its occurrence is drained without that scheduling token; the host shutdown grace bounds how long StopAsync waits.

Provider implementations are internal to trigger packages. IScheduleProvider is the public behavior seam for schedule discovery and per-schedule job enablement.

## Deferred behavior

Retries, misfire policies, durable history, leader election, and distributed worker ownership are not implemented. The application model therefore enforces a singleton workload.

## AOT posture

Runtime composition uses explicit registrations and immutable snapshots. It does not scan for jobs or activate them through reflection.
