# Assimalign.Cohesion.Web.ApplicationModel

The Web area's orchestration package. It has a deliberately small dependency
closure: `Assimalign.Cohesion.ApplicationModel` for declarative resource types
and `Assimalign.Cohesion.Hosting` for the Core-only resource control-plane
contract. It never references `Web.Hosting` or another Web feature package.

## Default control plane

`WebResourceControlPlane.Create()` creates the default control plane registered
by an enabled Web resource's generated `ResourceControlPlane.g.cs`. The Web host
serves that plane under `/cohesion/v1/*` on the resource's ambient `http`
endpoint. It includes aggregate health, readiness and liveness, observed
endpoints, graceful stop, and the Web area's command kinds. The initial Web
command-kind set is empty.

The package owns the surface because every enabled Web resource exposes the
same default. `Web.Hosting` consumes only `IResourceControlPlane` through
`ResourceRuntime`; the runtime therefore does not acquire an ApplicationModel
dependency.

## Opt-in boundary

The SDK injects this package and generates the registration only when the
consumer sets:

```xml
<CohesionApplicationModel>enabled</CohesionApplicationModel>
```

Without that opt-in, `WebApplication.CreateBuilder(args)` remains an ordinary
Web application: no registered control plane, no Cohesion terminal routes, and
the normal bodyless `404` fallback handles `/cohesion/v1/*` like any other
unmatched path.

## AOT posture

The control-plane factory uses static construction only. There is no assembly
scanning, dynamic activation, or runtime code generation.
