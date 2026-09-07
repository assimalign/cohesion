# Assimalign.Cohesion.Web.ApplicationModel - Design

## Intent

This project owns the Web resource area's typed orchestration model, portable planner,
and default control-plane factory. An enabled Web executable is a composition root:
generated `ResourceControlPlane.g.cs` references `WebResourceControlPlane.Create()`
and registers the factory against that executable's assembly through the Core-only
`ResourceRuntime` seam.

## Boundaries

- The project references only `Assimalign.Cohesion.ApplicationModel` and
  `Assimalign.Cohesion.Hosting`.
- It never references `Web.Hosting`, Web feature packages, or a platform package.
- `Web.Hosting` discovers the assembly-keyed registration through `ResourceRuntime`;
  it does not reference this project.
- Each factory call returns a new control plane so in-process resources cannot share
  health contributors, endpoints, or lifecycle state.

## Manifest-backed resource and options

`WebResource` derives from `PlannedResource`; the base snapshots the supplied
`ResourceManifest` and projects its executable, endpoint, and mount compatibility
surfaces. Artifact identity, endpoints, probes, mounts, lifecycle constraints,
settings, and references remain executable-owned manifest facts.

`WebResourceOptions` derives from the shared `ResourceOptions`. `Replicas` is a
deployer override validated against the manifest's `maxReplicas` at application
build. Public exposure remains a build-produced manifest fact in the signed
`cohesion/plan/v1` contract. The inherited storage slot is not meaningful for this
stateless kind, so a configured storage-size override is rejected rather than ignored.

`AddWeb(manifest, options)` adds the typed resource and keeps plan computation
deferred until `IApplicationBuilder.Build()`.

## Web planner

`WebResource.CreatePlan(PlanContext)` is identified as `Web planner` in build
diagnostics. It requires `Kind = "Web"`, a `Deployment` workload, and no persistent
`Volume` mounts. It inherits the generic endpoint, probe, environment, and mount
mapping, verifies that the result has no stable identity or volume claims and exactly
one ordinary service per endpoint, and preserves the generic public-exposure mapping.

The result is `cohesion/plan/v1` IR only. Kubernetes Ingresses, load balancers,
Docker port publication, local bindings, and certificate resolution are owned by the
selected platform compiler and its gateway inputs. The Web planner neither references
nor identifies a platform.

## Default control plane

The default Web plane aggregates `IHealthContributor` instances, reports observed
endpoints, accepts graceful-stop requests, and advertises the Web command kinds. The
command-kind set is empty until the declarative-command work adds the area-specific
handlers. `Web.Hosting` serves health probes at `/healthz`, `/readyz`, and `/livez`,
and management operations below `/cohesion/v1` on the ambient `http` endpoint.

## AOT posture

Construction, planning, and registration are static. Golden plan serialization uses
the source-generated `ResourcePlanJsonContext`. There is no assembly scanning,
reflection-based activation, or runtime code generation.
