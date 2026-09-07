# Assimalign.Cohesion.Web.ApplicationModel - Overview

The Web area's Core-only orchestration package supplies a typed manifest-backed
resource, a platform-neutral planner, and the default control-plane factory used by
enabled Web executables.

## Key surface

- `WebResource` snapshots a build-produced `ResourceManifest`.
- `WebResourceOptions` provides the typed `Replicas` override.
- `AddWeb(manifest, options)` composes the typed resource into an application graph.
- The Web planner emits `cohesion/plan/v1` IR: a stateless `Deployment`, no
  persistent volumes, one service per endpoint, and one exposure per public endpoint.
- `WebResourceControlPlane.Create()` returns a fresh `IResourceControlPlane`.
- The plane aggregates health, readiness, and liveness contributions.
- It reports realized endpoints and accepts graceful-stop requests.
- Its accepted command list is currently empty.

The Web SDK adds this package only for an executable with
`CohesionApplicationModel=enabled`. Generated code registers the factory by resource
assembly; `WebApplication.CreateBuilder(args)` consumes that registration and the
current `ResourceContext`. A disabled executable remains a plain Web application.

Public exposure remains a manifest fact. Plan v1 has no host or certificate override
fields; platform-specific ingress, load-balancer, and certificate-resolution choices
remain gateway responsibilities.

## Dependencies

- `Assimalign.Cohesion.ApplicationModel`
- `Assimalign.Cohesion.Hosting`

No Web runtime or platform assembly enters this dependency closure.
