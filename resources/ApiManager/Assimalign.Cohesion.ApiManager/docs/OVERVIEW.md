# Assimalign.Cohesion.ApiManager

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the ApiManager area. The implementation and creation entry point live in `Assimalign.Cohesion.ApiManager.Hosting`.

## Public surface

- `IApiManagerApplicationBuilder` extends the shared build-only host-builder contract, registers `IHostService` instances or context factories, and builds an `IApiManagerApplication`.
- `IApiManagerApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application remains a filler with no area behavior or hosted services registered by default. Consumers can add explicit lifecycle services through the area builder; API management behavior is outside this slice.
