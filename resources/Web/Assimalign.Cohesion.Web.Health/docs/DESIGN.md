# Assimalign.Cohesion.Web.Health — Design

## Design intent

One package that owns Cohesion health checks end to end: the health *model* (the
`IHealthCheck` contract, the `Healthy`/`Degraded`/`Unhealthy` report, a builder-time
registry) and its *delivery* (the `/healthz` · `/livez` · `/readyz` pipeline endpoint plus
an AOT-safe JSON writer). Health lives in the Web area because **health is delivered over
HTTP** — a Kubernetes liveness/readiness probe is an HTTP endpoint the kubelet scrapes, and
that is true even for services that are otherwise "headless." There is no separate,
transport-agnostic health library; carving one out would be weight the design doesn't need.

## Consumption model: opt-in options, private packaging

The app developer **never authors a health check** and never sees these types. A resource
(Database, LoadBalancer, …) ships built-in checks and exposes health purely through its own
options:

```csharp
// what the application developer writes — nothing health-typed:
builder.AddDatabase(options =>
{
    options.EnableHealthCheck = true;         // opt in
    options.HealthCheckPath   = "/healthz";   // small override
});
```

The resource consumes this package as a **private implementation detail**, exactly the
`Database`-privately-uses-`Web` cross-resource pattern (`.claude/rules/build-system.md`):

- `CohesionPrivateProjectReference Include="Assimalign.Cohesion.Web.Health"` in the resource
  csproj — compiled against + CopyLocal'd, but **not** a `<dependency>` in the resource's
  `.nupkg`.
- `CohesionFrameworkPrivateAssembly Include="Assimalign.Cohesion.Web.Health"` in the
  resource's `App.<Domain>` block — the DLL ships in the framework's Runtime pack
  (`RuntimeList.xml`) but is omitted from the Ref pack (`FrameworkList.xml`), so it never
  appears in the consumer's IntelliSense.

Net effect: the HTTP/Web stack is pulled into the resource at runtime, but the consumer is
unaware — they see only `EnableHealthCheck` and a path. The resource translates those options
to the wiring below internally.

### Why the model types are `public` but hidden

`IHealthCheck`, `HealthStatus`, `HealthReport`, and the registry are `public` — not for the
app developer, but because a **resource library lives in a different assembly** and must
compile its built-in checks (e.g. a connectivity probe) against them. `CohesionFramework-
PrivateAssembly` hides a dependency a consumer *uses*; it can't hide a type a consumer
*implements* — but here no consumer implements one. The resource does, privately, and
re-exposes only its own options. This is the one seam the private-packaging pattern can't
collapse, and it's why the types stay `public` rather than `internal`.

## Internal wiring (what a resource does)

When its `EnableHealthCheck` option is set, a resource composes its checks with the
container-free builder and maps the endpoint onto the Web pipeline:

```csharp
IHealthCheckService health = HealthChecks.CreateBuilder()
    .AddCheck("database", new DatabaseConnectivityCheck(...), tags: new[] { HealthTags.Ready })
    .Build();

pipeline.MapHealthChecks(options.HealthCheckPath ?? "/healthz", health);
pipeline.MapReadinessCheck(health);   // /readyz — ready-tagged
pipeline.MapLivenessCheck(health);    // /livez  — live-tagged
```

`HealthChecks.CreateBuilder()` is the container-free entry point; there is **no DI
registration surface** — the resource wires health imperatively at composition time, so the
model never takes a dependency on any container. The `IHealthCheckService` is supplied to
`MapHealthChecks` explicitly and built at composition time; the middleware never performs
request-time service location (Lane C/E guardrail).

## Status model and aggregation

`HealthStatus` is ordered least-to-most healthy — `Unhealthy = 0 < Degraded = 1 <
Healthy = 2` — so `HealthReport.Status` is the **minimum** across its entries and an **empty
report is `Healthy`**. That empty-is-healthy rule is what lets `/livez` report the process up
when no `live`-tagged checks are registered. The engine runs checks sequentially, folds a
thrown or timed-out check into its entry with the registration's `FailureStatus`, and lets
caller cancellation propagate. `Degraded` mirrors the ApplicationModel
`ResourceLifecycle.Degraded` state.

## Request handling

The mapped middleware:

1. Passes the request through untouched unless the path matches and the method is `GET`/`HEAD`.
2. Runs `service.CheckHealthAsync(options.Predicate, context.RequestCancelled)`.
3. Attaches an `IHttpHealthFeature` to `context.Features` (the `IHttpFeatureCollection`
   extensibility model) so other middleware can observe the report without re-running checks.
4. Maps aggregate status → HTTP status: `Healthy`/`Degraded` ⇒ 200, `Unhealthy` ⇒ 503 (all
   configurable). Degraded is 200 because a degraded instance is still serving.
5. Emits `Cache-Control: no-store, no-cache` unless caching is explicitly allowed.
6. Writes the body via `IHealthResponseWriter` and **short-circuits** — the endpoint is terminal.

## The JSON response writer (AOT-safe)

`HealthCheckJsonResponseWriter` (default) is hand-written over `Utf8JsonWriter` — no
reflection-based `JsonSerializer`, mirroring the `OpenApiJsonWriter` precedent — emitting
`application/health+json`. Diagnostic `data` values go through a closed type switch (string /
bool / numeric primitives), falling back to `ToString()` rather than a runtime serializer.
Pluggable via `HealthEndpointOptions.ResponseWriter`.

## Orchestration bridge

The control plane learns a resource's health by **scraping its HTTP endpoint** — the same way
the kubelet does — and maps the reported status onto `ResourceLifecycle` (`Healthy`→`Running`,
`Degraded`→`Degraded`, `Unhealthy`→`Degraded`/reschedule). Consumption is over HTTP; there is
no in-process push publisher (an earlier `IHealthPublisher` + `BackgroundService` design was
dropped as redundant with the scrape). The K8s gateway emits `/livez` (livenessProbe),
`/readyz` (readinessProbe), and `/healthz` (aggregate) in the Deployment spec.

## AOT posture

Reflection-free end to end. The package references only `Web` (no DI container), so the whole
request path — including serialization — is trim- and NativeAOT-clean.

## Framework packaging (deferred)

Not yet listed in `frameworks/Assimalign.Cohesion.App.props`. When a resource adopts health it
adds the `CohesionFrameworkPrivateAssembly` entry to *its own* `App.<Domain>` block (per the
pattern above); until a resource wires it, there is no framework membership to add. The Web
feature projects generally are still being assembled (see `docs/HTTP_WEB_PROGRAM_PLAN.md`).

## Non-goals

- **A separate transport-agnostic health library.** Health is HTTP-delivered; the model and
  endpoint ship together and are pulled privately by resources.
- **A DI registration surface / user-authored checks.** Consumers opt in via resource options;
  they never implement `IHealthCheck`. Resources compose built-in checks with
  `HealthChecks.CreateBuilder()`.
- **An in-process push publisher.** The control plane scrapes `/healthz` over HTTP.
- **Reflection-based serialization.** The default writer is hand-written; custom writers are a
  pluggable option.
- **Request-time service location.** `MapHealthChecks` takes a composition-time
  `IHealthCheckService`.
