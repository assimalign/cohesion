# Assimalign.Cohesion.Database.ApplicationModel — Overview

The AOT-compatible, dependency-guarded orchestration package for Cohesion databases. It turns an enabled
database executable's build-produced `ResourceManifest` into a typed
`DatabaseResource`, applies deployer-owned replica and storage overrides, and emits a
platform-neutral `ResourcePlan` for the selected gateway compiler.

```csharp
ResourceManifest manifest = ResourceManifest.Load("resource.json");

IDatabaseResourceDescriptor database = builder.AddDatabase(
    manifest,
    new DatabaseResourceOptions
    {
        Replicas = 2,
        Storage = { Size = "20Gi" },
    });
```

Generated gateway code normally supplies the manifest and exposes these typed options
to application authors.

## Scope

- `DatabaseResource` — a `PlannedResource` over the immutable manifest snapshot.
- `DatabaseResourceOptions` — typed `Replicas` and `Storage.Size` overrides.
- `AddDatabase(manifest, options)` — application-graph composition.
- Database planner — stable workload identity, sized per-replica volume claims, one
  service per endpoint, and a headless governing service.
- `DatabaseResourceControlPlane` — the Database default control-plane factory.

The control plane accepts `database.add-database` and `database.add-principal`. Typed descriptor verbs `AddDatabase(name, engine)` and `AddPrincipal(database, name)` record those declarations; an explicit engine gives the database an `engine/database` ownership key. `RemoteReferenceDatabase` supplies the same typed surface for manifest-backed externals. Principal creation receives a named rejection until the runtime supports principal mutation.
Database names cannot contain `/`, which keeps ownership keys unambiguous.

## Dependencies

- `Assimalign.Cohesion.ApplicationModel` for manifests, planned resources, and the
  platform-neutral realization-plan IR.
- `Assimalign.Cohesion.Hosting.Resources` for the default control-plane contract and runtime seam.

The project is guarded by COHAM001 and never references Database runtime, gateway, or
platform packages. It emits no legacy resource-specific environment variables; runtime
endpoint and mount values flow through the `Hosting.Resources` `ResourceContext`.
