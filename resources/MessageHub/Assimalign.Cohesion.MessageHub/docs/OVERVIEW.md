# Assimalign.Cohesion.MessageHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the MessageHub area. The implementation and creation entry point live in `Assimalign.Cohesion.MessageHub.Hosting`.

## Public surface

- `IMessageHubApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `IMessageHubApplication`.
- `IMessageHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; message-broker behavior remains outside this slice.
