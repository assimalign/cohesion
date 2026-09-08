# Assimalign.Cohesion.SecretStore

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the SecretStore area. The implementation and creation entry point live in `Assimalign.Cohesion.SecretStore.Hosting`.

## Public surface

- `ISecretStoreApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `ISecretStoreApplication`.
- `ISecretStoreApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; secret persistence, trust, and certificate behavior remain outside this slice.
