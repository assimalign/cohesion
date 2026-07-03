# Assimalign.Cohesion.ApiManager.Hosting

## Summary

Standalone hosting application for the API gateway and management plane resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- ApiManagerApplication owns the resource process lifecycle (start, run, stop) via Host<ApiManagerApplicationContext>.
- ApiManagerApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: GatewayEndpointService (pooled).

## Key Types

- ApiManagerApplication
- ApiManagerApplicationContext
- ApiManagerApplicationOptions