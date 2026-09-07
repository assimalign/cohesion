# Assimalign.Cohesion.ConfigurationStore

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the ConfigurationStore area. The implementation and creation entry point live in `Assimalign.Cohesion.ConfigurationStore.Hosting`.

## Public surface

- `IConfigurationStoreApplicationBuilder` extends the shared host-builder contract and builds an `IConfigurationStoreApplication`.
- `IConfigurationStoreApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Configuration-store behavior is outside this slice.
