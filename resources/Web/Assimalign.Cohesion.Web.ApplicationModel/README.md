# Assimalign.Cohesion.Web.ApplicationModel

The Web area's AOT-compatible, dependency-guarded orchestration package. It turns a build-produced
`ResourceManifest` into a typed `WebResource`, applies deployer-owned options,
and emits the platform-neutral `cohesion/plan/v1` realization plan consumed by
the selected gateway compiler. It also supplies the default Web resource
control plane.

Its direct dependency set is deliberately small: `Assimalign.Cohesion.ApplicationModel`
for manifests and realization-plan records, and `Assimalign.Cohesion.Hosting.Resources`
for the resource control-plane contract and runtime registration seam. It never references
`Web.Hosting`, another Web feature package, or a platform SDK.

## Planning a Web resource

`WebResource` accepts only a `Kind = "Web"`, stateless `Deployment` manifest.
Persistent `Volume` mounts are rejected. Every manifest endpoint becomes one
service, and every endpoint marked `Public = true` becomes one exposure.

```csharp
ResourceManifest manifest = ResourceManifest.Load("resource.json");
var options = new WebResourceOptions { Replicas = 3 };

IApplicationResourceDescriptor api = builder.AddWeb(manifest, options);
```

Public exposure remains a build-produced manifest fact in plan v1. Host and
certificate overrides are not added to the plan because they are not fields in
the signed `cohesion/plan/v1` contract; platform-specific ingress or
load-balancer settings never enter this package.

## Default control plane

`WebResourceControlPlane.Create()` creates the default control plane registered
by an enabled Web resource's generated `ResourceControlPlane.g.cs`. The Web host
serves that plane under `/cohesion/v1/*` on the resource's ambient `http`
endpoint. It includes aggregate health, readiness and liveness, observed
endpoints, graceful stop, and the Web area's command kinds. The initial Web
command-kind set is empty.

The package owns the surface because every enabled Web resource exposes the
same default. `Web.Hosting` consumes only the `Hosting.Resources`
`IResourceControlPlane` through `ResourceRuntime`; the runtime therefore does not
acquire an ApplicationModel dependency.

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

The resource, planner, options, and control-plane factory use static typed
construction only. Golden serialization uses `ResourcePlanJsonContext`. There
is no assembly scanning, reflection-based activation, or runtime code generation.
