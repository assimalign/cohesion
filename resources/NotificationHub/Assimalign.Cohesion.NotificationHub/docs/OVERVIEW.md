# Assimalign.Cohesion.NotificationHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the NotificationHub area. The implementation and creation entry point live in `Assimalign.Cohesion.NotificationHub.Hosting`.

## Public surface

- `INotificationHubApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `INotificationHubApplication`.
- `INotificationHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; notification delivery behavior remains outside this slice.
