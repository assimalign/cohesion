# Assimalign.Cohesion.LogSpace

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the LogSpace area. The implementation and creation entry point live in `Assimalign.Cohesion.LogSpace.Hosting`.

## Public surface

- `ILogSpaceApplicationBuilder` extends the shared build-only host-builder contract, registers `IHostService` instances or context factories, and builds an `ILogSpaceApplication`.
- `ILogSpaceApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application remains a filler with no area behavior or hosted services registered by default. Consumers can add explicit lifecycle services through the area builder; log-storage behavior is outside this slice.
