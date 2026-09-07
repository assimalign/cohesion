# Assimalign.Cohesion.MessageHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the MessageHub area. The implementation and creation entry point live in `Assimalign.Cohesion.MessageHub.Hosting`.

## Public surface

- `IMessageHubApplicationBuilder` extends the shared host-builder contract and builds an `IMessageHubApplication`.
- `IMessageHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Message-broker behavior is outside this slice.
