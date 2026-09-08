# Assimalign.Cohesion.SecretStore.Hosting

## Summary

Provides the public `SecretStoreApplication.CreateBuilder(args)` entry point and the internal filler implementation of the SecretStore application contracts.

## Current evaluation

- Status: composition-capable filler with no default services
- Project references: Assimalign.Cohesion.SecretStore and Assimalign.Cohesion.Hosting

## Primary responsibilities

- Validate application arguments and return the area-root builder interface.
- Materialize caller-registered service instances and context-aware factories into an ordered lifecycle snapshot for an internal `Host<SecretStoreApplicationContext>`.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `SecretsEndpointService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `SecretStoreApplication` — static creation facade; all runtime implementation types are internal.
