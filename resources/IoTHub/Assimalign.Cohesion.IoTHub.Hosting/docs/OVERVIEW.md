# Assimalign.Cohesion.IoTHub.Hosting

## Summary

Provides the public `IoTHubApplication.CreateBuilder(args)` entry point and the internal filler implementation of the IoTHub application contracts.

## Current Evaluation

- Status: contract-only filler with an empty lifecycle
- Project references: Assimalign.Cohesion.IoTHub and Assimalign.Cohesion.Hosting

## Primary Responsibilities

- Validate application arguments and return the area-root builder interface.
- Build an internal `Host<IoTHubApplicationContext>` with a production environment and no hosted services.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `TelemetryJournalService` and `DeviceIngressService` future-service stubs remain in the project but are not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `IoTHubApplication` — static creation facade; all runtime implementation types are internal.
