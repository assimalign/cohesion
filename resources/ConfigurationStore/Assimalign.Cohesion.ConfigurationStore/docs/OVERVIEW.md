# Assimalign.Cohesion.ConfigurationStore

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the ConfigurationStore area. The implementation and creation entry point live in `Assimalign.Cohesion.ConfigurationStore.Hosting`.

## Public surface

- `IConfigurationStoreApplicationBuilder` extends the shared build-only host-builder contract, registers `IHostService` instances or context factories, and builds an `IConfigurationStoreApplication`.
- `IConfigurationStoreApplicationBuilder.AddNamespace` declares a durable namespace's first-start values.
- `IConfigurationNamespaceBuilder.Set` declares a string or null entry.
- `IConfigurationStoreApplication` exposes the shared host lifecycle plus `RunAsync`.

The concrete Hosting implementation adds the protocol endpoint automatically. Explicit lifecycle
services still compose through the area-root interface and remain independent of Hosting internals.
