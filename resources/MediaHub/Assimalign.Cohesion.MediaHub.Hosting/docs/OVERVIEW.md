# Assimalign.Cohesion.MediaHub.Hosting

## Summary

Standalone hosting application for the media hub resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- MediaHubApplication owns the resource process lifecycle (start, run, stop) via Host<MediaHubApplicationContext>.
- MediaHubApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: ContentIoService (dedicated), StreamingEndpointService (pooled).

## Key Types

- MediaHubApplication
- MediaHubApplicationContext
- MediaHubApplicationOptions