# Assimalign.Cohesion.EventHub.Hosting

## Summary

Provides the public `EventHubApplication.CreateBuilder(args)` entry point and the internal filler implementation of the EventHub application contracts.

## Current Evaluation

- Status: contract-only filler with an empty lifecycle
- Project references: Assimalign.Cohesion.EventHub and Assimalign.Cohesion.Hosting

## Primary Responsibilities

- Validate application arguments and return the area-root builder interface.
- Build an internal `Host<EventHubApplicationContext>` with a production environment and no hosted services.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `PartitionFlushService` and `IngressEndpointService` future-service stubs remain in the project but are not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `EventHubApplication` — static creation facade; all runtime implementation types are internal.
