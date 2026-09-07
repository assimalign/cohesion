# Assimalign.Cohesion.ApiManager.Hosting

## Summary

Provides the public `ApiManagerApplication.CreateBuilder(args)` entry point and the internal filler implementation of the ApiManager application contracts.

## Current Evaluation

- Status: contract-only filler with an empty lifecycle
- Project references: Assimalign.Cohesion.ApiManager and Assimalign.Cohesion.Hosting

## Primary Responsibilities

- Validate application arguments and return the area-root builder interface.
- Build an internal `Host<ApiManagerApplicationContext>` with a production environment and no hosted services.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `GatewayEndpointService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `ApiManagerApplication` — static creation facade; all runtime implementation types are internal.
