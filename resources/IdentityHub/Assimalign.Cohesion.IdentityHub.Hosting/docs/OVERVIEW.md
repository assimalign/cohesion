# Assimalign.Cohesion.IdentityHub.Hosting

## Summary

Standalone hosting application for the identity provider resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- IdentityHubApplication owns the resource process lifecycle (start, run, stop) via Host<IdentityHubApplicationContext>.
- IdentityHubApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: IdentityEndpointService (pooled).

## Key Types

- IdentityHubApplication
- IdentityHubApplicationContext
- IdentityHubApplicationOptions