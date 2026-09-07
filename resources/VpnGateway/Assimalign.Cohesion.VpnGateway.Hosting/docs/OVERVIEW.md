# Assimalign.Cohesion.VpnGateway.Hosting

## Summary

Provides the public `VpnGatewayApplication.CreateBuilder(args)` entry point and the internal filler implementation of the VpnGateway application contracts.

## Current evaluation

- Status: contract-only filler with an empty lifecycle
- Project references: Assimalign.Cohesion.VpnGateway and Assimalign.Cohesion.Hosting

## Primary responsibilities

- Validate application arguments and return the area-root builder interface.
- Build an internal `Host<VpnGatewayApplicationContext>` with a production environment and no hosted services.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `TunnelDataPlaneService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `VpnGatewayApplication` — static creation facade; all runtime implementation types are internal.
