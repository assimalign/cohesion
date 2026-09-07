# Assimalign.Cohesion.EventHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the EventHub area. The implementation and creation entry point live in `Assimalign.Cohesion.EventHub.Hosting`.

## Public surface

- `IEventHubApplicationBuilder` extends the shared host-builder contract and builds an `IEventHubApplication`.
- `IEventHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Event-hub behavior is outside this slice.
