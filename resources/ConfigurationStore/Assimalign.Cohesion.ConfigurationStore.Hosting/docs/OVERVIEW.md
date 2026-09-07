# Assimalign.Cohesion.ConfigurationStore.Hosting

## Summary

Provides the public `ConfigurationStoreApplication.CreateBuilder(args)` entry point and the internal filler implementation of the ConfigurationStore application contracts.

## Current Evaluation

- Status: contract-only filler with an empty lifecycle
- Project references: Assimalign.Cohesion.ConfigurationStore and Assimalign.Cohesion.Hosting

## Primary Responsibilities

- Validate application arguments and return the area-root builder interface.
- Build an internal `Host<ConfigurationStoreApplicationContext>` with a production environment and no hosted services.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `ConfigurationEndpointService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `ConfigurationStoreApplication` — static creation facade; all runtime implementation types are internal.
