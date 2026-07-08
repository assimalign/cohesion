# Assimalign.Cohesion.Web.Health — Overview

## Purpose

The HTTP health endpoint for Cohesion Web applications: maps `/healthz`, `/livez`, and
`/readyz` onto the `Assimalign.Cohesion.Health` model with tag-based readiness/liveness
filtering, 200/503 status mapping, and a pluggable AOT-safe JSON response writer.

## Scope

- `MapHealthChecks` / `MapReadinessCheck` / `MapLivenessCheck` — pipeline-builder members.
- `HealthEndpointOptions` — predicate, response writer, status-code mapping, caching.
- `IHealthResponseWriter` + the default `application/health+json` `Utf8JsonWriter` writer.
- `IHttpHealthFeature` — surfaces the produced report via `IHttpFeatureCollection`.

## Dependencies

`Assimalign.Cohesion.Web`, `Assimalign.Cohesion.Health`. No DI container reference.

## Layering

L3 Web feature project (`resources/Web`). Consumes the L1 Health model and the Web pipeline;
composed by the application on top of `Assimalign.Cohesion.Web.Hosting`.

## Usage

```csharp
IHealthCheckService health = app.Context.ServiceProvider.GetRequiredService<IHealthCheckService>();
app.MapHealthChecks(health);      // /healthz  (all checks, 200/503)
app.MapReadinessCheck(health);    // /readyz   (ready-tagged)
app.MapLivenessCheck(health);     // /livez    (live-tagged)
```

See `docs/DESIGN.md` for the composition model and the AOT-safe writer.
