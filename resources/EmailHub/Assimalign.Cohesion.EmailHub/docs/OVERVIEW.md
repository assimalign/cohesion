# Assimalign.Cohesion.EmailHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the EmailHub area. The implementation and creation entry point live in `Assimalign.Cohesion.EmailHub.Hosting`.

## Public surface

- `IEmailHubApplicationBuilder` extends the shared host-builder contract and builds an `IEmailHubApplication`.
- `IEmailHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Email-hub behavior is outside this slice.
