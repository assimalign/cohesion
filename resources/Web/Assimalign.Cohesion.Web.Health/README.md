# Assimalign.Cohesion.Web.Health

Public HTTP health checks for Cohesion Web applications. The package provides the health model,
builder-time registration, readiness/liveness filtering, `/healthz`, `/readyz`, and `/livez`
pipeline endpoints, and an AOT-safe JSON response writer.

`App.Web` exposes this assembly publicly, so application authors can register their own
`IHealthCheck` implementations, inline probes, or transport-neutral `Hosting.Health`
`IHealthContributor` instances through Web.Hosting's adapter in the
`Assimalign.Cohesion.Web.Hosting.Health` namespace.
All 17 other area frameworks, including `App.Database`, carry the health-model assembly
privately as part of Web.Hosting's runtime closure; that private inclusion does not expose
the Web health API to those applications.

```csharp
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting.Health;

IHealthCheckService health = new HealthChecksBuilder()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy(),
        tags: new[] { HealthTags.Ready, HealthTags.Live })
    .AddContributor(databaseContributor)
    .Build();

pipeline.MapHealthChecks(health);
pipeline.MapReadinessCheck(health);
pipeline.MapLivenessCheck(health);
```

`AddContributor` adapts the contributor's name, status, description, data, and cancellation
contract. Contributors participate in readiness and liveness by default. Supply custom tags to
select a different slice, or an empty collection to include a contributor only in aggregate
health.

The JSON writer publishes no exception text: a check that throws or times out reports its status
and a fixed description, and its exception stays on the in-process report. Set
`HealthEndpointOptions.IncludeDeveloperDetails` in development to add each exception's message
to the response.

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).

Hosting contributor integration ships in `Assimalign.Cohesion.Web.Hosting`, using the
`Assimalign.Cohesion.Web.Hosting.Health` namespace; no separate adapter package is needed.
`Web.Health` owns the health model and endpoints and references no hosting library (O34).
