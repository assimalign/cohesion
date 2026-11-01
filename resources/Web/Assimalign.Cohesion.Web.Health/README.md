# Assimalign.Cohesion.Web.Health

Public HTTP health checks for Cohesion Web applications. The package provides the health model,
builder-time registration, readiness/liveness filtering, `/healthz`, `/readyz`, and `/livez`
pipeline endpoints, and an AOT-safe JSON response writer.

`App.Web` exposes this assembly publicly, so application authors can register their own
`IHealthCheck` implementations and inline probes.
`App.Database` carries the health-model assembly privately because Database.Hosting references
it; that private inclusion does not expose the Web health API to Database applications. No other
area framework carries it.

```csharp
using Assimalign.Cohesion.Web.Health;

IHealthCheckService health = new HealthChecksBuilder()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy(),
        tags: new[] { HealthTags.Ready, HealthTags.Live })
    .Build();

pipeline.MapHealthChecks(health);
pipeline.MapReadinessCheck(health);
pipeline.MapLivenessCheck(health);
```

The JSON writer publishes no exception text: a check that throws or times out reports its status
and a fixed description, and its exception stays on the in-process report. Set
`HealthEndpointOptions.IncludeDeveloperDetails` in development to add each exception's message
to the response.

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).

`Web.Health` owns the health model and endpoints and references no hosting library (O34).
