# Assimalign.Cohesion.Health — Design

## Design intent

One shared health-check model for every Cohesion service — the same contracts a
resource host, the Web `/healthz` endpoint, and the orchestration plane all program
against. Before this library each of the 18 resource hosts would have grown its own
ad-hoc health surface; the K8s self-registry gateway design (ApplicationModel
`DESIGN.md` §7) assumes per-resource health but nothing in-process produced the signal.

The core library is intentionally narrow and **dependency-free**:

- One check contract — `IHealthCheck.CheckAsync` returning a `HealthCheckResult`.
- One three-state status — `HealthStatus` (`Healthy`/`Degraded`/`Unhealthy`).
- One aggregate result — `HealthReport` over per-check `HealthReportEntry` values.
- One builder-time registry — `IHealthChecksBuilder` → immutable `IHealthCheckService`.
- One publish seam — `IHealthPublisher` (the periodic driver lives in `.Hosting`).

It deliberately follows Cohesion's lean philosophy rather than ASP.NET's
Abstractions + Core + AspNetCore three-package split: **one library, interface-first,
internal implementations, builder-time registration only, AOT-safe.**

## Family map

| Package | Role | Dependencies |
|---------|------|--------------|
| `Assimalign.Cohesion.Health` | Contracts, report model, registry, check engine | *(none)* |
| `Assimalign.Cohesion.Health.Hosting` | Periodic publisher (`BackgroundService`) + DI extensions | `Health`, `Hosting`, `DependencyInjection` |
| `Assimalign.Cohesion.Web.Health` | `/healthz` / `/livez` / `/readyz` pipeline endpoint + JSON writer | `Health`, `Web` |

Dependency direction is one-way. The core has **zero** project references — a resource
host can consume the model without pulling in DI, hosting, or HTTP. The DI/Logging/Config
seam and the host-lifecycle wiring live only in `.Hosting`, matching the repo-wide rule
that `*.Hosting` is the sole DI seam and core libraries stay DI-free.

## Why interface-first (not an abstract base)

Unlike the DNS family (uniform protocol implementations, so an abstract base owning
disposal plumbing pays off), health checks are heterogeneous: a check may be a tiny
lambda, a class holding a connection, or an adapter over an existing probe. There is no
shared lifecycle plumbing to hoist into a base. So `IHealthCheck` stays a bare interface
and the concrete engine/registry/builder are `internal` — the canonical Cohesion
interface-first shape. `HealthChecks.CreateBuilder()` is the container-free entry point;
`AddHealthChecks()` in `.Hosting` is the DI entry point.

## Status model and aggregation

`HealthStatus` is ordered least-to-most healthy on purpose:

```
Unhealthy = 0  <  Degraded = 1  <  Healthy = 2
```

so `HealthReport.Status` is simply the **minimum** across its entries, and an **empty
report aggregates to `Healthy`**. That empty-is-healthy rule is what makes a liveness
probe that runs no checks report the process as up. The numeric ordering is part of the
contract — the members must not be renumbered.

`Degraded` mirrors the ApplicationModel `ResourceLifecycle.Degraded` ("running but
unhealthy") state, which is the whole reason the middle state exists: the control plane
already models it (`ResourceLifecycle.cs`) but had no in-process producer.

## Check engine (`HealthCheckService`)

Checks run **sequentially**. Health checks are expected to be cheap and few; sequential
execution keeps ordering deterministic and avoids a burst of concurrent probes hammering
shared dependencies. The engine's contract for a single check:

1. Build a `HealthCheckContext` carrying the registration (so a check can read its own
   name/tags/timeout).
2. If the registration has a per-check `Timeout`, link a `CancellationTokenSource` off the
   caller token and `CancelAfter` it.
3. Run the check and fold its `HealthCheckResult` into a `HealthReportEntry` with the
   measured duration and the registration's tags.

Failure translation is the subtle part and is locked down by tests:

- **The check throws** → the entry takes the registration's `FailureStatus`
  (default `Unhealthy`, configurable to `Degraded` for a soft dependency) and captures the
  exception. One bad check never throws out of `CheckHealthAsync`.
- **The per-check timeout trips** (caller token still live) → the entry takes
  `FailureStatus` with a "timed out" description. Detected with
  `catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)`.
- **The caller cancels** (host shutdown / request abort) → the `OperationCanceledException`
  propagates out of `CheckHealthAsync`. This is deliberately *not* folded into an entry:
  caller cancellation means "stop", not "this check is unhealthy".

## Registration and immutability

`HealthCheckRegistration` is immutable and holds a `Func<IHealthCheck>` factory rather than
an instance, so a host can hand back a fresh or DI-resolved check per evaluation without the
core taking a DI dependency. Registrations are composed at builder time and frozen when
`IHealthChecksBuilder.Build()` snapshots them into the service — **there is no runtime
mutation surface.** Duplicate names are rejected (case-insensitive) at registration time.

`Build()` snapshots lazily-by-convention: in the DI flow `AddHealthChecks()` registers
`IHealthCheckService` as a factory that calls `Build()` the first time the service is
resolved, so every `AddCheck` made during host build is captured.

## Tags: readiness vs. liveness

Tags are free-form strings on a registration. Two are well-known (`HealthTags.Ready`,
`HealthTags.Live`) and drive the standard Kubernetes probe split:

- **Readiness** (`/readyz`) gates on `ready`-tagged checks — the dependencies (DB, cache,
  downstream service) that must be up before traffic is routed. A failing readiness check
  removes the pod from the Service endpoints without restarting it.
- **Liveness** (`/livez`) gates on `live`-tagged checks — cheap in-process invariants. A
  failing liveness check restarts the pod. With no live-tagged checks the probe reports the
  process up (empty report → `Healthy`), which is the correct default.

Selection is a `Func<HealthCheckRegistration, bool>` passed to `CheckHealthAsync`.
`HealthCheckPredicates` provides `Ready`, `Live`, and `WithAnyTag(...)`.

## Publish seam and the orchestration bridge

`IHealthPublisher` is how health flows **out** of a process. The periodic driver lives in
`.Hosting`; publishers are pluggable sinks. The canonical consumer is the control plane:

| `HealthStatus` | `ResourceLifecycle` |
|----------------|---------------------|
| `Healthy` | `Running` |
| `Degraded` | `Degraded` |
| `Unhealthy` | `Degraded` (→ restart / re-schedule by the gateway) |

This mapping is implemented **as a publisher**, not as a dependency: the health library
never references ApplicationModel. A future ApplicationModel-side publisher implements
`IHealthPublisher`, maps the aggregate status onto `ResourceLifecycle`, and feeds the state
manager — "no reverse reference" per the issue. The default probe endpoints a Kubernetes
gateway would emit in a Deployment spec are `/livez` (livenessProbe), `/readyz`
(readinessProbe), and `/healthz` (aggregate), served by `Assimalign.Cohesion.Web.Health`.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited from the `libraries/` tree. The core
surface avoids reflection entirely:

- No reflection-based serialization — the report model is plain data; the JSON writer
  (in `Web.Health`) is hand-written over `Utf8JsonWriter` (the `OpenApiJsonWriter`
  precedent).
- No runtime type discovery — checks are registered explicitly, never scanned for.
- No open generics requiring reflection emit.

The one AOT caveat is transitive: `Health.Hosting` references
`Assimalign.Cohesion.DependencyInjection`, whose container uses expression/IL fallbacks that
emit trim/AOT analyzer warnings. That is a property of the shared DI container, not of the
health code, and only affects the `.Hosting` seam — the core library and the Web endpoint
stay clean.

## Framework packaging (deferred)

The three assemblies are intentionally **not** yet added to
`frameworks/Assimalign.Cohesion.App.props`. Web feature projects (`Web.Forms`, `Web.Cors`,
…) and the `Http.*` libraries are likewise still commented out of the `App.Web` block while
the Web resource is assembled (see `docs/HTTP_WEB_PROGRAM_PLAN.md`). Health joins the
framework manifest as part of that assembly milestone, alongside its Web peers, so the
membership stays consistent rather than shipping one feature ahead of the rest.

## Non-goals

- **A DI container dependency in the core.** Registration is builder-time and
  container-free; the DI extensions live in `.Hosting` only.
- **Request-time service location in the endpoint.** The Web endpoint takes an
  `IHealthCheckService` resolved at build time; it never resolves per request.
- **A pull of ASP.NET's package layout.** One lean library, not
  Abstractions + Core + AspNetCore.
- **Concurrent check execution / scheduling policies in v1.** Checks run sequentially; a
  future opt-in could parallelize, but the default stays deterministic.
- **Reverse dependency on ApplicationModel.** The orchestration bridge is a publisher the
  control plane implements, not a reference the health library holds.
- **Jitter on the publish interval in v1.** `Delay` is the startup offset and `Period` the
  interval; adding jitter is a later, additive option.
