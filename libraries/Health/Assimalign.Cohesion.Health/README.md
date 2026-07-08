# Assimalign.Cohesion.Health

The health-check foundation for Cohesion: the `IHealthCheck` contract, the
`Healthy`/`Degraded`/`Unhealthy` report model, a builder-time registry, tag-based
readiness/liveness filtering, and the `IHealthPublisher` seam. Zero dependencies, AOT-safe.

- Compose checks container-free with `HealthChecks.CreateBuilder()`, or through
  `AddHealthChecks()` in `Assimalign.Cohesion.Health.Hosting`.
- Run them with `IHealthCheckService.CheckHealthAsync(predicate)`; the report's aggregate
  status is the worst entry (empty ⇒ `Healthy`).
- The periodic publisher lives in `Assimalign.Cohesion.Health.Hosting`; the `/healthz`
  endpoint lives in `Assimalign.Cohesion.Web.Health`.

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
