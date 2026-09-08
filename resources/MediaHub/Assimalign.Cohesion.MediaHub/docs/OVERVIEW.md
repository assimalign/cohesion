# Assimalign.Cohesion.MediaHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the MediaHub area. The implementation and creation entry point live in `Assimalign.Cohesion.MediaHub.Hosting`.

## Public surface

- `IMediaHubApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `IMediaHubApplication`.
- `IMediaHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; media-hub behavior remains outside this slice.
