# Assimalign.Cohesion.Web.Health — Overview

## Purpose

`Assimalign.Cohesion.Web.Health` is the public health-check feature for Cohesion Web
applications. It owns both the health model and its HTTP delivery through `/healthz`, `/readyz`,
and `/livez` pipeline endpoints.

## Scope

- **Model:** `IHealthCheck`, `HealthStatus`, `HealthCheckResult`, `HealthReport`,
  `HealthReportEntry`, and `HealthCheckRegistration`.
- **Composition:** `new HealthChecksBuilder()`, `IHealthChecksBuilder`, inline and typed checks.
  Web.Hosting supplies `AddContributor` for host contributors in its `.Hosting.Health` namespace.
- **Filtering:** `HealthTags` and `HealthCheckPredicates` for aggregate, readiness, and liveness
  views.
- **HTTP delivery:** `MapHealthChecks`, `MapReadinessCheck`, `MapLivenessCheck`,
  `HealthEndpointOptions`, `IHealthResponseWriter`, and `IHealthFeature`.

## Dependencies

The package references `Assimalign.Cohesion.Web` for pipeline and HTTP contracts. It references
no hosting library or dependency-injection container. `Web.Hosting` references this package and
`Hosting.Health` to supply its integrated contributor bridge (owner decision 57, 2026-10-13).

## Framework delivery

- `App.Web` lists `Assimalign.Cohesion.Web.Health` as a public framework assembly. Web application
  authors see and use its types directly.
- All 17 other area frameworks list the same assembly privately because they carry Web.Hosting
  and its implementation closure. Their application authors do not gain a public Web health
  surface from that inclusion; decision 57 adds the 16 frameworks besides App.Database.

The package can also be referenced directly outside those framework profiles.

## Usage

Compose an immutable service, then explicitly map the endpoints that the application exposes:

```csharp
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting.Health;

IHealthCheckService health = new HealthChecksBuilder()
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

### Exception detail stays out of the response

The default JSON writer publishes each check's status, duration, description, tags and data, and
no exception text. A check that throws or times out is described by fixed text
(`The check threw an exception.` or `The check timed out.`), and a returned description that
contains its exception's message, or an exception placed in `data`, is replaced. Keep connection
detail out of the descriptions and data a check returns.

In development, opt in to the exception messages per endpoint:

```csharp
pipeline.MapHealthChecks(health, options =>
    options.IncludeDeveloperDetails = builder.Environment.IsDevelopment());
```

`IncludeDeveloperDetails` adds an `exception` member with each failed entry's exception message,
writes descriptions as the checks returned them, and writes an exception in `data` as its message.
It writes no exception type or stack trace, and it does not affect a custom `ResponseWriter`. It
does not restore the 10.0.0-preview.1 output: a thrown or timed-out check keeps its fixed
description either way ([breaking changes](DESIGN.md#exception-detail)). The exception object itself is always available
in process, through `IHealthFeature.Report` or a custom `IHealthResponseWriter`
([DESIGN](DESIGN.md#exception-detail)).

Hosting contributor integration ships in `Assimalign.Cohesion.Web.Hosting`, using the
`Assimalign.Cohesion.Web.Hosting.Health` namespace; no separate adapter package is needed.
`Web.Health` owns the health model and endpoints and references no hosting library (O34).

### Server-side diagnostics

Listen to `Assimalign.Cohesion.Web.Health` with `dotnet-trace`, `dotnet-counters`, or the
event-source logging forwarder. It reports status worsening and recovery per service instance,
Verbose thrown/timed-out probes, and the exact `failed-checks` total. Health exceptions include
their type and message in server-side sinks; dependency messages can contain host, database or
user names. HTTP response sanitization is independent. See [Diagnostics](DESIGN.md#diagnostics).


## Health-check builder construction (#1470)

Composition uses the public sealed `HealthChecksBuilder` in `Assimalign.Cohesion.Web.Health`,
with its public parameterless constructor. The standalone `HealthChecks.CreateBuilder()` factory
is removed. Existing `IHealthChecksBuilder` adapters, duplicate-name checks and immutable service
snapshots are preserved; Database's admin endpoint uses the same public constructor. No Database
interface, protocol, format, diagnostic code, event or counter is added or changed.
