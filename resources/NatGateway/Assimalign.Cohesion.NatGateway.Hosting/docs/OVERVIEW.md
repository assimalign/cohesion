# Assimalign.Cohesion.NatGateway.Hosting

## Summary

Standalone hosting application for the NAT gateway resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- NatGatewayApplication owns the resource process lifecycle (start, run, stop) via Host<NatGatewayApplicationContext>.
- NatGatewayApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: TranslationDataPlaneService (dataplane).

## Key Types

- NatGatewayApplication
- NatGatewayApplicationContext
- NatGatewayApplicationOptions