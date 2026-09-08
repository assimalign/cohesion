# Assimalign.Cohesion.EmailHub.Hosting

## Summary

Provides the public `EmailHubApplication.CreateBuilder(args)` entry point and the internal filler implementation of the EmailHub application contracts.

## Current Evaluation

- Status: contract-only filler with an explicit host-service lifecycle seam
- Project references: Assimalign.Cohesion.EmailHub and Assimalign.Cohesion.Hosting

## Primary Responsibilities

- Validate application arguments and return the area-root builder interface.
- Build an internal `Host<EmailHubApplicationContext>` with a production environment and the ordered services explicitly registered on the area builder.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `MailEndpointService` future-service stub remains in the project but is not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `EmailHubApplication` — static creation facade; all runtime implementation types are internal.
