# Assimalign.Cohesion.ConfigurationStore.Hosting

## Summary

Standalone hosting application for the configuration store resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- ConfigurationStoreApplication owns the resource process lifecycle (start, run, stop) via Host<ConfigurationStoreApplicationContext>.
- ConfigurationStoreApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: ConfigurationEndpointService (pooled).

## Key Types

- ConfigurationStoreApplication
- ConfigurationStoreApplicationContext
- ConfigurationStoreApplicationOptions