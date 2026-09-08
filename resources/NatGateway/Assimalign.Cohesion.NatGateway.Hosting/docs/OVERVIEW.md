# Assimalign.Cohesion.NatGateway.Hosting

## Summary

Provides the public `NatGatewayApplication.CreateBuilder(args)` entry point and the internal filler implementation of the NatGateway application contracts.

## Current Evaluation

- Status: composition-capable filler with no default services
- Project references: Assimalign.Cohesion.NatGateway and Assimalign.Cohesion.Hosting

## Primary Responsibilities

- Validate application arguments and return the area-root builder interface.
- Materialize caller-registered service instances and context-aware factories into an ordered lifecycle snapshot for an internal `Host<NatGatewayApplicationContext>`.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `TranslationDataPlaneService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `NatGatewayApplication` — static creation facade; all runtime implementation types are internal.
