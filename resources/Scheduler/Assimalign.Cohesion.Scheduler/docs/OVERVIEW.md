# Assimalign.Cohesion.Scheduler

## Summary

This project contains scheduler contracts and defines the public, contract-only builder and application lifecycle seam for the Scheduler area. The application implementation and creation entry point live in `Assimalign.Cohesion.Scheduler.Hosting`.

## Application surface

- `ISchedulerApplicationBuilder` extends the shared host-builder contract and builds an `ISchedulerApplication`.
- `ISchedulerApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Scheduling behavior is outside this slice.
