# Assimalign.Cohesion.MediaHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the MediaHub area. The implementation and creation entry point live in `Assimalign.Cohesion.MediaHub.Hosting`.

## Public surface

- `IMediaHubApplicationBuilder` extends the shared host-builder contract and builds an `IMediaHubApplication`.
- `IMediaHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Media-hub behavior is outside this slice.
