# Assimalign.Cohesion.Rezolvr.Hosting

## Summary

Provides the public `RezolvrApplication.CreateBuilder(args)` entry point and the internal filler implementation of the Rezolvr application contracts.

## Current evaluation

- Status: composition-capable filler with no default services
- Project references: Assimalign.Cohesion.Rezolvr and Assimalign.Cohesion.Hosting

## Primary responsibilities

- Validate application arguments and return the area-root builder interface.
- Materialize caller-registered service instances and context-aware factories into an ordered lifecycle snapshot for an internal `Host<RezolvrApplicationContext>`.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `ResolverEndpointService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `RezolvrApplication` — static creation facade; all runtime implementation types are internal.
