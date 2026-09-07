# Assimalign.Cohesion.IdentityHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the IdentityHub area alongside its existing identity domain contracts. The implementation and creation entry point live in `Assimalign.Cohesion.IdentityHub.Hosting`.

## Public application surface

- `IIdentityHubApplicationBuilder` extends the shared host-builder contract and builds an `IIdentityHubApplication`.
- `IIdentityHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Identity-provider behavior is outside this slice.
