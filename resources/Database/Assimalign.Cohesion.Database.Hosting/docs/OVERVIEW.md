# Assimalign.Cohesion.Database.Hosting

## Summary

Standalone hosting application for the database engine resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Database, Assimalign.Cohesion.Hosting

## Primary Responsibilities

- DatabaseApplication owns the resource process lifecycle (start, run, stop) via Host<DatabaseApplicationContext>.
- DatabaseApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: WriteAheadFlushService (dedicated), PageWriterService (dedicated), QueryEndpointService (pooled).

## Key Types

- DatabaseApplication
- DatabaseApplicationContext
- DatabaseApplicationOptions