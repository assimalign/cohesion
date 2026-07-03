# Assimalign.Cohesion.LoadBalancer.Hosting

## Summary

Standalone hosting application for the load balancer resource: a `Host<TContext>` subclass composing the resource's units of work as hosted services on the per-service execution model.

## Current Evaluation

- Status: Scaffold (execution model selected and documented; service bodies are placeholders)
- Project references: Assimalign.Cohesion.Hosting

## Primary Responsibilities

- LoadBalancerApplication owns the resource process lifecycle (start, run, stop) via Host<LoadBalancerApplicationContext>.
- LoadBalancerApplicationContext carries the environment and the composed hosted services.
- Internal services select their execution base per the menu: ProxyDataPlaneService (dataplane).

## Key Types

- LoadBalancerApplication
- LoadBalancerApplicationContext
- LoadBalancerApplicationOptions