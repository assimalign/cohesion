# Assimalign.Cohesion.LogSpace

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the LogSpace area. The implementation and creation entry point live in `Assimalign.Cohesion.LogSpace.Hosting`.

## Public surface

- `ILogSpaceApplicationBuilder` extends the shared host-builder contract and builds an `ILogSpaceApplication`.
- `ILogSpaceApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Log-storage behavior is outside this slice.
