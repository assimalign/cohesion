# Assimalign.Cohesion.Rezolvr.Hosting

## Summary

Provides the public `RezolvrApplication.CreateBuilder(args)` entry point and the internal filler implementation of the Rezolvr application contracts.

## Current evaluation

- Status: contract-only filler with an empty lifecycle
- Project references: Assimalign.Cohesion.Rezolvr and Assimalign.Cohesion.Hosting

## Primary responsibilities

- Validate application arguments and return the area-root builder interface.
- Build an internal `Host<RezolvrApplicationContext>` with a production environment and no hosted services.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `ResolverEndpointService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `RezolvrApplication` — static creation facade; all runtime implementation types are internal.
