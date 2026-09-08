# Scheduler

Scheduler is the L3 operational service platform intended to coordinate schedules, jobs, triggers, durable execution, retries, misfire handling, and distributed worker ownership.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.Scheduler` contains the existing scheduler domain contracts and the public area-root application and builder contracts.
- `Assimalign.Cohesion.Scheduler.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.
- `Assimalign.Cohesion.Scheduler.Cron` provides the existing cron schedule and crontab model.
- `Assimalign.Cohesion.Scheduler.Timer` reserves the timer-based scheduling implementation and is currently project scaffolding.

## Layering and dependencies

As an L3 service platform, Scheduler composes the L2 `Assimalign.Cohesion.Hosting` runtime and also references the L1 `Assimalign.Cohesion.Core` foundation directly. Cron depends on the area root, while the Hosting project depends only on the area root and shared Hosting runtime.

## Project documentation

- [Root overview](./Assimalign.Cohesion.Scheduler/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.Scheduler/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.Scheduler.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.Scheduler.Hosting/docs/DESIGN.md)
