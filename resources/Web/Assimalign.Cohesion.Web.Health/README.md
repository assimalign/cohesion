# Assimalign.Cohesion.Web.Health

The HTTP health endpoint for Cohesion Web applications. Maps `/healthz`, `/livez`, and
`/readyz` onto the `Assimalign.Cohesion.Health` model with tag-based readiness/liveness
filtering, 200/503 status mapping, and a pluggable AOT-safe (`Utf8JsonWriter`) JSON writer.
The produced report is surfaced through `IHttpFeatureCollection` as `IHttpHealthFeature`.

```csharp
IHealthCheckService health = app.Context.ServiceProvider.GetRequiredService<IHealthCheckService>();
app.MapHealthChecks(health);
app.MapReadinessCheck(health);
app.MapLivenessCheck(health);
```

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
