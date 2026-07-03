# Assimalign.Cohesion.LogSpace.Hosting

## Summary

Standalone hosting application for the log storage engine resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- LogSpaceApplication owns the resource process lifecycle (start, run, stop) via Host<LogSpaceApplicationContext>.
- LogSpaceApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: SegmentFlushService (dedicated), IngestEndpointService (pooled).

## Key Types

- LogSpaceApplication
- LogSpaceApplicationContext
- LogSpaceApplicationOptions