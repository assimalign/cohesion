# Assimalign.Cohesion.Web.Health

Public HTTP health checks for Cohesion Web applications. The package provides the health model,
builder-time registration, readiness/liveness filtering, `/healthz`, `/readyz`, and `/livez`
pipeline endpoints, and an AOT-safe JSON response writer.

`App.Web` exposes this assembly publicly, so application authors can register their own
`IHealthCheck` implementations, inline probes, or transport-neutral `Hosting.Health`
`IHealthContributor` instances. `App.Database` also carries the assembly privately as part of
its Web runtime closure; that private inclusion does not expose the Web health API to Database
applications.

```csharp
IHealthCheckService health = HealthChecks.CreateBuilder()
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

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
