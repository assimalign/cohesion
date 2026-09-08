# Assimalign.Cohesion.EmailHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the EmailHub area. The implementation and creation entry point live in `Assimalign.Cohesion.EmailHub.Hosting`.

## Public surface

- `IEmailHubApplicationBuilder` extends the shared build-only host-builder contract, registers `IHostService` instances or context factories, and builds an `IEmailHubApplication`.
- `IEmailHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application remains a filler with no area behavior or hosted services registered by default. Consumers can add explicit lifecycle services through the area builder; email-hub behavior is outside this slice.
