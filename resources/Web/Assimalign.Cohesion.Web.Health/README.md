# Assimalign.Cohesion.Web.Health

Cohesion health checks, delivered over HTTP. One package owns the health model (`IHealthCheck`,
`Healthy`/`Degraded`/`Unhealthy` report, a builder-time registry, readiness/liveness tag
filtering) and the `/healthz` · `/livez` · `/readyz` pipeline endpoint with an AOT-safe
(`Utf8JsonWriter`) response writer. The produced report is surfaced through
`IHttpFeatureCollection` as `IHttpHealthFeature`.

Health lives in the Web area because it is delivered over HTTP (Kubernetes probes are HTTP
endpoints). Resources consume this package as a **private implementation detail**
(`CohesionPrivateProjectReference` + `CohesionFrameworkPrivateAssembly`) and expose health only
through their own options (`EnableHealthCheck`, path) — the application developer never sees
these types.

```csharp
// inside a resource, when its EnableHealthCheck option is set:
IHealthCheckService health = HealthChecks.CreateBuilder()
    .AddCheck("database", new DatabaseConnectivityCheck(...), tags: new[] { HealthTags.Ready })
    .Build();
pipeline.MapHealthChecks(options.HealthCheckPath ?? "/healthz", health);
```

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
