# Assimalign.Cohesion.Web.Health — Overview

## Purpose

`Assimalign.Cohesion.Web.Health` is the public health-check feature for Cohesion Web
applications. It owns both the health model and its HTTP delivery through `/healthz`, `/readyz`,
and `/livez` pipeline endpoints.

## Scope

- **Model:** `IHealthCheck`, `HealthStatus`, `HealthCheckResult`, `HealthReport`,
  `HealthReportEntry`, and `HealthCheckRegistration`.
- **Composition:** `HealthChecks.CreateBuilder()`, `IHealthChecksBuilder`, inline and typed checks,
  and `AddContributor` for `Assimalign.Cohesion.Hosting.IHealthContributor`.
- **Filtering:** `HealthTags` and `HealthCheckPredicates` for aggregate, readiness, and liveness
  views.
- **HTTP delivery:** `MapHealthChecks`, `MapReadinessCheck`, `MapLivenessCheck`,
  `HealthEndpointOptions`, `IHealthResponseWriter`, and `IHttpHealthFeature`.

## Dependencies

The package references `Assimalign.Cohesion.Web` for pipeline and HTTP contracts and
`Assimalign.Cohesion.Hosting` for the transport-neutral contributor bridge. It does not depend on
a dependency-injection container.

## Framework delivery

- `App.Web` lists `Assimalign.Cohesion.Web.Health` as a public framework assembly. Web application
  authors see and use its types directly.
- `App.Database` lists the same assembly as a private framework assembly because the Database
  runtime uses a private Web implementation closure. Database application authors do not gain a
  public Web health surface from that inclusion.

The package can also be referenced directly outside those framework profiles.

## Usage

Compose an immutable service, then explicitly map the endpoints that the application exposes:

```csharp
IHealthCheckService health = HealthChecks.CreateBuilder()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy("accepting traffic"),
        tags: new[] { HealthTags.Ready, HealthTags.Live })
    .AddContributor(resourceContext)
    .Build();

pipeline.MapHealthChecks(health);       // /healthz, all checks
pipeline.MapReadinessCheck(health);     // /readyz, ready-tagged checks
pipeline.MapLivenessCheck(health);      // /livez, live-tagged checks
```

`AddContributor` uses `IHealthContributor.Name` as the registration name and preserves the
contribution's status, description, and diagnostic data. It forwards request cancellation and
supports the same failure status, tags, and timeout policy as `AddCheck`. When tags are omitted,
both `ready` and `live` are applied; pass an empty collection for aggregate-only participation.

The middleware receives `IHealthCheckService` explicitly. It does not locate services during a
request and does not mutate registrations after the service is built.
