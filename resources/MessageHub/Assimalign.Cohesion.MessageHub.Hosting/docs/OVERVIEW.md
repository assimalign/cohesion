# Assimalign.Cohesion.MessageHub.Hosting

## Summary

Provides the public `MessageHubApplication.CreateBuilder(args)` entry point and the internal filler implementation of the MessageHub application contracts.

## Current Evaluation

- Status: composition-capable filler with no default services
- Project references: Assimalign.Cohesion.MessageHub and Assimalign.Cohesion.Hosting

## Primary Responsibilities

- Validate application arguments and return the area-root builder interface.
- Materialize caller-registered service instances and context-aware factories into an ordered lifecycle snapshot for an internal `Host<MessageHubApplicationContext>`.
- Preserve the hosting-isolation boundary and an AOT-safe construction path.

The old `JournalFlushService` and `BrokerEndpointService` future-service stubs remain in the project but are not registered. Ambient `ResourceRuntime` integration is deferred to design item 12.

## Public type

- `MessageHubApplication` — static creation facade; all runtime implementation types are internal.
