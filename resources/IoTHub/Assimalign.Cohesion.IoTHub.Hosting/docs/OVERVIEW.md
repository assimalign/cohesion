# Assimalign.Cohesion.IoTHub.Hosting

## Summary

Standalone hosting application for the IoT device hub resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- IoTHubApplication owns the resource process lifecycle (start, run, stop) via Host<IoTHubApplicationContext>.
- IoTHubApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: TelemetryJournalService (dedicated), DeviceIngressService (pooled).

## Key Types

- IoTHubApplication
- IoTHubApplicationContext
- IoTHubApplicationOptions