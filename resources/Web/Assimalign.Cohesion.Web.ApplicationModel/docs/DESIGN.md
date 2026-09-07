# Assimalign.Cohesion.Web.ApplicationModel - Design

## Intent

This project owns the Web resource area's orchestration model and its default
control-plane factory. An enabled Web executable is a composition root: generated
`ResourceControlPlane.g.cs` references `WebResourceControlPlane.Create()` and registers
the factory against that executable's assembly through the Core-only
`ResourceRuntime` seam.

## Boundaries

- The project references only `Assimalign.Cohesion.ApplicationModel` and
  `Assimalign.Cohesion.Hosting`.
- It never references `Web.Hosting`, Web feature packages, or a platform package.
- `Web.Hosting` discovers the assembly-keyed registration through `ResourceRuntime`;
  it does not reference this project.
- Each factory call returns a new control plane so in-process resources cannot share
  health contributors, endpoints, or lifecycle state.

## Default control plane

The default Web plane aggregates `IHealthContributor` instances, reports observed
endpoints, accepts graceful-stop requests, and advertises the Web command kinds. The
command-kind set is empty until the declarative-command work adds the area-specific
handlers. `Web.Hosting` serves health probes at `/healthz`, `/readyz`, and `/livez`,
and management operations below `/cohesion/v1` on the ambient `http` endpoint.

## AOT posture

Construction and registration are static. There is no assembly scanning, dynamic
activation, or runtime code generation.
