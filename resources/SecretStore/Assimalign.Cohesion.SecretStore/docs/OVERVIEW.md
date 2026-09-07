# Assimalign.Cohesion.SecretStore

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the SecretStore area. The implementation and creation entry point live in `Assimalign.Cohesion.SecretStore.Hosting`.

## Public surface

- `ISecretStoreApplicationBuilder` extends the shared host-builder contract and builds an `ISecretStoreApplication`.
- `ISecretStoreApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Secret persistence, trust, and certificate behavior are outside this slice.
