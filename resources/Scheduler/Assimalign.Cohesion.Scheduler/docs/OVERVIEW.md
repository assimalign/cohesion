# Assimalign.Cohesion.Scheduler

## Summary

This project contains scheduler contracts and defines the public, contract-only builder and application lifecycle seam for the Scheduler area. The application implementation and creation entry point live in `Assimalign.Cohesion.Scheduler.Hosting`.

## Application surface

- `ISchedulerApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `ISchedulerApplication`.
- `ISchedulerApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; scheduling behavior remains outside this slice.
