# Assimalign.Cohesion.Web.Health — Overview

## Purpose

Cohesion health checks, delivered over HTTP. One package owns both the health model and the
`/healthz` · `/livez` · `/readyz` endpoint. Health lives in the Web area because it is
delivered over HTTP (Kubernetes probes are HTTP endpoints), and resources consume it as a
private implementation detail — the application developer opts in via resource options and
never sees these types.

## Scope

- **Model:** `IHealthCheck`, `HealthStatus`, `HealthCheckResult`, `HealthReport`,
  `HealthReportEntry`, `HealthCheckRegistration`, `IHealthChecksBuilder`,
  `IHealthCheckService`, `HealthChecks.CreateBuilder()`, `HealthTags`, `HealthCheckPredicates`.
- **Endpoint:** `MapHealthChecks` / `MapReadinessCheck` / `MapLivenessCheck`,
  `HealthEndpointOptions`, `IHealthResponseWriter` + the default AOT-safe JSON writer,
  `IHttpHealthFeature`.

## Dependencies

`Assimalign.Cohesion.Web` (which brings the HTTP stack). No DI container reference.

## Layering

L3 Web feature project. Resources reference it **privately**
(`CohesionPrivateProjectReference` + `CohesionFrameworkPrivateAssembly`) so the HTTP stack is
pulled into the resource at runtime but hidden from the resource's consumers.

## Usage

An application developer never touches these types — they opt in through a resource's options:

```csharp
builder.AddDatabase(o => { o.EnableHealthCheck = true; o.HealthCheckPath = "/healthz"; });
```

A resource wires it internally when that option is set:

```csharp
IHealthCheckService health = HealthChecks.CreateBuilder()
    .AddCheck("database", new DatabaseConnectivityCheck(...), tags: new[] { HealthTags.Ready })
    .Build();
pipeline.MapHealthChecks(o.HealthCheckPath ?? "/healthz", health);
```

See `docs/DESIGN.md` for the private-packaging model and the AOT-safe writer.
