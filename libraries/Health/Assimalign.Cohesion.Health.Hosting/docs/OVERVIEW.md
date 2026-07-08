# Assimalign.Cohesion.Health.Hosting — Overview

## Purpose

The hosting seam for health checks: the periodic publisher that rides the Hosting execution
menu and the builder-time DI extensions that compose the check set and register publishers.

## Scope

- `HealthCheckPublisherOptions` — interval (`Period`), startup offset (`Delay`), per-cycle
  `Timeout`, and check `Predicate`.
- `AddHealthChecks(...)` — registers the health-check service and the periodic publisher host
  service; returns the `IHealthChecksBuilder` for `AddCheck` chaining.
- `AddHealthCheckPublisher(...)` — registers `IHealthPublisher` sinks.

## Dependencies

`Assimalign.Cohesion.Health`, `Assimalign.Cohesion.Hosting`,
`Assimalign.Cohesion.DependencyInjection`.

## Layering

L1 hosting seam. This is the only place health touches DI; the core stays container-free.

## Usage

```csharp
services.AddHealthChecks(options =>
    {
        options.Period = TimeSpan.FromSeconds(15);
        options.Predicate = HealthCheckPredicates.Ready;
    })
    .AddCheck("db", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Ready });

services.AddHealthCheckPublisher<ResourceLifecyclePublisher>(); // maps status → the control plane
```

See `docs/DESIGN.md` for the publish-loop and DI-composition rationale.
