# Assimalign.Cohesion.Database.ApplicationModel — Design

## Intent

This project is the Database area's Core-only orchestration package. It gives a
gateway a typed database resource, a platform-neutral realization planner, and the
default control-plane factory that every orchestration-enabled database executable
registers. It never references `Database.Hosting`, an engine package, a gateway
implementation, or a platform object model.

An enabled `Sdk.Database` executable produces `cohesion/resource/v1` at build time.
Generated gateway code passes that `ResourceManifest` to `AddDatabase(...)`; the
ApplicationModel package does not reconstruct endpoint, mount, artifact, or lifecycle
facts from conventions.

## Manifest-backed resource

`DatabaseResource` derives from `PlannedResource`. The base snapshots the supplied
`ResourceManifest` and projects its executable, endpoint, and mount compatibility
surfaces. The manifest remains the source of truth for:

- executable/image identity;
- the `db` and `admin` endpoints and their probes;
- the persistent mounts and their declared sizes;
- lifecycle limits, application identity, settings, and references; and
- the default control-plane location.

The older constructor that accepted only a resource name and synthesized an
`Assimalign.Cohesion.Database.Application` artifact has been removed. That executable
was an interim framework-owned apphost; resource identity now belongs to the
customer's generated manifest.

`DatabaseResourceOptions` derives from the shared `ResourceOptions`. It deliberately
exposes only deployer-owned planning overrides:

- `Replicas` overrides `manifest.lifecycle.replicas`, subject to `maxReplicas` during
  model validation.
- `Storage.Size` overrides each persistent claim's `mounts[].size` value.

Ports, mount paths, durability settings, and arbitrary environment values are resource
facts owned by the executable manifest, not deployer options. Consequently this
package no longer declares or emits resource-specific environment variables. Runtime
values flow through the frozen `ResourceContext` contract instead.

## Database planner

`DatabaseResource.CreatePlan(PlanContext)` is reported as `Database planner` in the
application build diagnostic. The planner enforces the Database area's persistent
shape, then uses the shared trait mapper to produce `cohesion/plan/v1`:

- the workload is a `StatefulSet` with stable replica identity;
- every `Volume` mount becomes a sized, per-replica claim;
- each manifest endpoint becomes a stable service; and
- exactly one portless, headless governing service anchors replica identity.

The result contains only Cohesion realization-plan records. Kubernetes claims,
services, StatefulSets, Docker volumes, and local directories are produced later by
the selected platform compiler. A checked-in golden plan pins the complete Database
IR shape so compiler-facing changes are explicit in review.

## Composition API

`AddDatabase(manifest, options)` creates a `DatabaseResource`, applies typed replica
and storage overrides, and adds it to the application graph. Planning remains deferred
until `IApplicationBuilder.Build()` so the planner receives the selected environment
and the final referenced-manifest map.

```csharp
ResourceManifest manifest = ResourceManifest.Load("resource.json");

IApplicationResourceDescriptor database = builder.AddDatabase(
    manifest,
    new DatabaseResourceOptions
    {
        Replicas = 2,
        Storage = { Size = "20Gi" },
    });
```

Generated gateway verbs supply the manifest and expose the same optional typed options;
application authors normally do not load the JSON themselves.

## Default control plane

`DatabaseResourceControlPlane.Create()` returns a fresh Core-only
`IResourceControlPlane`. The enabled resource's generated
`ResourceControlPlane.g.cs` registers that factory with `ResourceRuntime` and seeds it
with the invocation's observed endpoints. `Database.Hosting` reads the registration
through the shared Hosting contract and serves it on the manifest's `admin` endpoint;
neither package references the other.

The accepted command-kind set is intentionally empty in this work item. Database
resource commands (`AddDatabase`, `AddPrincipal`) belong to developer-experience item
31c and must not be introduced early.

## Dependency and AOT posture

COHAM001 constrains the complete production dependency closure to
`Assimalign.Cohesion.Core`, `Assimalign.Cohesion.ApplicationModel`,
`Assimalign.Cohesion.Hosting`, and the permitted BCL surface. The planner uses typed
records and ordinary loops only. Golden serialization goes through
`ResourcePlanJsonContext`; there is no reflection, assembly scanning, runtime code
generation, runtime database dependency, or platform SDK dependency.

## Non-goals

- Hosting engines, protocol servers, health endpoints, or database provisioning.
- Inventing a manifest from a resource name or an executable-name convention.
- Carrying platform-specific scheduling, storage-class, service, or claim types.
- Connection settings or client factories; those belong to `Database.Client`.
- Database resource command verbs before item 31c.
