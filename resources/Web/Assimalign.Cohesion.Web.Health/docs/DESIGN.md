# Assimalign.Cohesion.Web.Health — Design

## Design intent

One Web feature owns Cohesion health checks end to end: the application-facing health model, a
builder-time registry, readiness/liveness selection, HTTP pipeline endpoints, and a reflection-free
JSON response writer. Keeping registration and delivery together gives Web applications one small,
explicit API without introducing a DI-specific hosting layer.

## Public application composition

Web application and resource authors can register typed checks, inline probes, or
`Hosting.Health` contributors through the optional `Web.Hosting.Health` package:

```csharp
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting.Health;

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

`HealthChecks.CreateBuilder()` is the container-free composition seam. Registrations are mutable
only while composing the builder; `Build()` snapshots them into an immutable
`IHealthCheckService`. The service is supplied to endpoint middleware explicitly, so request-time
service location is unnecessary.

## Hosting contributor integration

The `AddContributor` adapter lives in `Assimalign.Cohesion.Web.Hosting.Health` (O34).
That hosting-family library references this model and `Hosting.Health`; this package
references only the Web root. No root or feature library may reference the adapter.

## Status model and aggregation

`HealthStatus` is ordered least to most healthy: `Unhealthy = 0`, `Degraded = 1`, and
`Healthy = 2`. A report's aggregate status is the minimum across its entries. An empty report is
healthy, allowing `/livez` or `/readyz` to remain meaningful when their tag slice has no entries.

Checks run sequentially. A thrown or timed-out check becomes an entry with the registration's
`FailureStatus`; caller cancellation propagates instead of being converted into a health result.

## Request handling

Mapped health middleware:

1. Passes through requests whose path or method does not match.
2. Runs `IHealthCheckService.CheckHealthAsync` with the endpoint predicate and request cancellation.
3. Attaches `IHttpHealthFeature` so other HTTP components can inspect the completed report.
4. Maps `Healthy` and `Degraded` to 200 and `Unhealthy` to 503 by default.
5. Disables response caching unless explicitly allowed.
6. Writes the response and terminates the matching pipeline branch.

`MapHealthChecks` selects every registration. `MapReadinessCheck` selects the `ready` tag, and
`MapLivenessCheck` selects the `live` tag.

## Response serialization and AOT

The default `HealthCheckJsonResponseWriter` uses `Utf8JsonWriter`, not reflection-based
serialization. Diagnostic data is handled through a closed primitive-value switch with a string
fallback. Custom behavior is available through `IHealthResponseWriter`.

The registration, evaluation, and default response path remains trim- and NativeAOT-compatible.
The optional contributor adapter has its own hosting-family package and tests.

## Framework packaging

Framework membership is current, not deferred:

- `App.Web` includes `Assimalign.Cohesion.Web.Health` as a public
  `CohesionFrameworkAssembly`, exposing it to Web application consumers.
- `App.Database` includes it as a private `CohesionFrameworkPrivateAssembly` alongside its private
  Web runtime closure. That inclusion supports Database runtime implementation without exposing
  Web health types through the Database reference surface.

A resource may still reference Web.Health privately when it needs the implementation internally,
but that is not the only consumption model and does not change App.Web's public API.

## Non-goals

- **A DI-specific registration surface.** Composition remains explicit and container-neutral.
- **Runtime registry mutation.** The built service is an immutable snapshot.
- **Request-time service location.** Endpoint middleware receives the service directly.
- **An in-process health publisher.** This package evaluates and serves health; it does not publish
  control-plane state.
- **Reflection-based response serialization.** The default response path is statically authored.
