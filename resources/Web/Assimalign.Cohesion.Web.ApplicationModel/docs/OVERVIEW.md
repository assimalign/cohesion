# Assimalign.Cohesion.Web.ApplicationModel - Overview

The Web area's ApplicationModel package supplies the declarative Web resource model
and `WebResourceControlPlane`, the default control-plane factory used by enabled Web
executables.

## Key surface

- `WebResourceControlPlane.Create()` returns a fresh `IResourceControlPlane`.
- The plane aggregates health, readiness, and liveness contributions.
- It reports realized endpoints and accepts graceful-stop requests.
- Its accepted command list is currently empty.

The Web SDK adds this package only for an executable with
`CohesionApplicationModel=enabled`. Generated code registers the factory by resource
assembly; `WebApplication.CreateBuilder(args)` consumes that registration and the
current `ResourceContext`. A disabled executable remains a plain Web application.

## Dependencies

- `Assimalign.Cohesion.ApplicationModel`
- `Assimalign.Cohesion.Hosting`

No Web runtime or platform assembly enters this dependency closure.
