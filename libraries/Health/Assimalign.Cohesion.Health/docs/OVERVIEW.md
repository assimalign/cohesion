# Assimalign.Cohesion.Health — Overview

## Purpose

The shared health-check foundation for Cohesion services: contracts, a three-state status
model, a builder-time registry, tag-based readiness/liveness filtering, and a publish seam.
Zero dependencies, AOT-safe.

## Scope

- `IHealthCheck` — the check contract.
- `HealthStatus` / `HealthCheckResult` / `HealthReport` / `HealthReportEntry` — the result model.
- `HealthCheckRegistration` + `IHealthChecksBuilder` — builder-time composition.
- `IHealthCheckService` — runs the registered checks and aggregates a report.
- `HealthTags` + `HealthCheckPredicates` — the readiness/liveness slices.
- `IHealthPublisher` — the seam the periodic publisher (in `.Hosting`) drives.

## Dependencies

None. The core library is deliberately dependency-free so any resource host can consume the
model without pulling in DI, hosting, or HTTP.

## Layering

L1 foundation library. The DI/host wiring lives in `Assimalign.Cohesion.Health.Hosting`; the
HTTP endpoint lives in `Assimalign.Cohesion.Web.Health`.

## Usage

```csharp
IHealthCheckService service = HealthChecks.CreateBuilder()
    .AddCheck("db", async (context, ct) =>
    {
        return await CanReachDatabaseAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("cannot reach database");
    }, tags: new[] { HealthTags.Ready })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Live })
    .Build();

HealthReport report = await service.CheckHealthAsync(HealthCheckPredicates.Ready);
// report.Status is the worst entry status; Healthy when empty.
```

In a hosted application, compose through the DI extension instead
(`Assimalign.Cohesion.Health.Hosting`):

```csharp
builder.Services.AddHealthChecks()
    .AddCheck("db", ..., tags: new[] { HealthTags.Ready });
```

See `docs/DESIGN.md` for the rationale behind the shape.
