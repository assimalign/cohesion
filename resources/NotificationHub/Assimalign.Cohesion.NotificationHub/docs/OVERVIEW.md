# Assimalign.Cohesion.NotificationHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the NotificationHub area. The implementation and creation entry point live in `Assimalign.Cohesion.NotificationHub.Hosting`.

## Public surface

- `INotificationHubApplicationBuilder` extends the shared host-builder contract and builds an `INotificationHubApplication`.
- `INotificationHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Notification delivery behavior is outside this slice.
