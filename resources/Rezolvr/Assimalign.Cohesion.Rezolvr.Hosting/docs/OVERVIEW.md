# Assimalign.Cohesion.Rezolvr.Hosting

## Summary

Standalone hosting application for the name resolver resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- RezolvrApplication owns the resource process lifecycle (start, run, stop) via Host<RezolvrApplicationContext>.
- RezolvrApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: ResolverEndpointService (pooled).

## Key Types

- RezolvrApplication
- RezolvrApplicationContext
- RezolvrApplicationOptions