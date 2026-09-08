# Assimalign.Cohesion.IdentityHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the IdentityHub area alongside its existing identity domain contracts. The implementation and creation entry point live in `Assimalign.Cohesion.IdentityHub.Hosting`.

## Public application surface

- `IIdentityHubApplicationBuilder` extends the shared build-only host-builder contract, registers `IHostService` instances or context factories, and builds an `IIdentityHubApplication`.
- `IIdentityHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application remains a filler with no area behavior or hosted services registered by default. Consumers can add explicit lifecycle services through the area builder; identity-provider behavior is outside this slice.
