# Assimalign.Cohesion.Web.Health — Design

## Design intent

The HTTP surface of the health model: a Web feature project that maps `/healthz`, `/livez`,
and `/readyz` onto an `IHealthCheckService` and writes the report as AOT-safe JSON. It is a
thin feature project — it consumes the `Assimalign.Cohesion.Health` model and the
`Assimalign.Cohesion.Web` pipeline and adds no health logic of its own.

## Composition (Lane E: consume a primitive + the pipeline; no request-time service location)

The endpoint is mapped onto the `IWebApplicationPipelineBuilder` that the Web server
(`Assimalign.Cohesion.Web.Hosting`, rewritten under #762) implements. The
`IHealthCheckService` is supplied **explicitly** and resolved at build time — the middleware
never performs request-time service location, per the Lane C/E guardrails.

```csharp
builder.Services.AddHealthChecks()                      // Health.Hosting (DI seam)
    .AddCheck("db", ..., tags: new[] { HealthTags.Ready });

WebApplication app = builder.Build();

IHealthCheckService health = app.Context.ServiceProvider
    .GetRequiredService<IHealthCheckService>();          // build-time resolve

app.MapHealthChecks(health);                             // /healthz — all checks
app.MapReadinessCheck(health);                           // /readyz  — ready-tagged
app.MapLivenessCheck(health);                            // /livez   — live-tagged
```

`Web.Hosting` deliberately does **not** reference this project — feature projects layer on
top of the host, they are not part of it (the same reason `Web.Hosting` doesn't reference
`Web.Forms`). "Composed via Web.Hosting" means the endpoint plugs into the pipeline
`Web.Hosting` provides, and the application composes it.

## Request handling

The mapped middleware:

1. Passes the request through untouched unless the path matches and the method is `GET`/`HEAD`
   — a health endpoint is idempotent and safe.
2. Runs `service.CheckHealthAsync(options.Predicate, context.RequestCancelled)`.
3. Attaches an `IHttpHealthFeature` to `context.Features` so other middleware can observe the
   report without re-running the checks (the `IHttpFeatureCollection` extensibility model,
   not `Items`).
4. Maps aggregate status → HTTP status: `Healthy`/`Degraded` ⇒ 200, `Unhealthy` ⇒ 503 (all
   configurable). Degraded is 200 by default because a degraded instance is still serving.
5. Emits `Cache-Control: no-store, no-cache` unless caching is explicitly allowed, so probes
   always observe fresh status.
6. Writes the body via the configured `IHealthResponseWriter` and **short-circuits** — a
   health endpoint is terminal.

## Readiness vs. liveness

`MapReadinessCheck` and `MapLivenessCheck` are `MapHealthChecks` with the
`HealthCheckPredicates.Ready` / `.Live` predicate pre-applied and default paths `/readyz` /
`/livez`. With no `live`-tagged checks, `/livez` returns an empty report — which aggregates to
`Healthy` (200) — reporting the process as up. This is the Kubernetes probe split: readiness
gates traffic; liveness gates restarts.

## The JSON response writer (AOT-safe)

`HealthCheckJsonResponseWriter` (the default) is hand-written over `Utf8JsonWriter` — no
reflection-based `JsonSerializer`, mirroring the `OpenApiJsonWriter` precedent — so it is
trim- and NativeAOT-safe. It emits `application/health+json` with the shape:

```json
{
  "status": "Degraded",
  "totalDurationMs": 12.4,
  "entries": {
    "db": {
      "status": "Degraded",
      "durationMs": 8.1,
      "description": "slow",
      "tags": ["ready", "sql"],
      "data": { "latencyMs": 250 }
    }
  }
}
```

Diagnostic `data` values are written through a closed type switch (string / bool / the
numeric primitives); anything else falls back to `ToString()` rather than pulling in a runtime
serializer. The writer is pluggable via `HealthEndpointOptions.ResponseWriter` for callers who
want a different format.

## AOT posture

Reflection-free end to end. The project references only `Web` and `Health` (no DI container),
so it carries none of the DI library's trim warnings — the whole request path, including
serialization, is AOT-clean.

## Framework packaging (deferred)

Not yet listed in `frameworks/Assimalign.Cohesion.App.props`'s `App.Web` block — the other
Web feature projects (`Web.Forms`, `Web.Cors`, …) aren't either while the Web resource is
assembled (see `docs/HTTP_WEB_PROGRAM_PLAN.md`). It joins the manifest with its peers at that
milestone.

## Non-goals

- **Request-time service location.** The `IHealthCheckService` is a build-time parameter.
- **Routing integration.** The endpoint is path-matched middleware; when the Web routing
  surface (#28) lands it may additionally expose a `MapGet`-style registration, but the
  middleware form has no routing dependency.
- **Reflection-based serialization.** The default writer is hand-written; custom writers are a
  pluggable option, not a serializer swap.
