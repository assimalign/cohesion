# Assimalign.Cohesion.Web.ApplicationModel

The Web area's AOT-compatible, dependency-guarded orchestration package. It turns a build-produced
`ResourceManifest` into a typed `WebResource`, applies deployer-owned options,
and emits the platform-neutral `cohesion/plan/v1` realization plan consumed by
the selected gateway compiler. It preserves producer-owned runtime metadata as opaque JSON.

This package describes resource manifests, dependencies and deployment plans. Its only direct Cohesion dependency is ApplicationModel, whose contracts depend only on Core. Sdk.ApplicationModel generates control-plane registration in the enabled resource executable; Hosting.Resources and the resource host own the runtime surface and apply the deployment record. The gateway observes infrastructure and delivers the record.

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
