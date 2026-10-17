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

A gateway normally passes the manifest `Sdk.Gateway` generates for the database project
(`builder.AddDatabase(Manifests.OrdersDatabase)`) instead of loading the JSON itself.

## Scope

- `DatabaseResource` — a `PlannedResource` over the immutable manifest snapshot.
- `DatabaseResourceOptions` — typed `Replicas` and `Storage.Size` overrides.
- `AddDatabase(manifest, options)` — application-graph composition.
- Database planner — stable workload identity, sized per-replica volume claims, one
  service per endpoint, and a headless governing service.
- `DatabaseResourceControlPlane` — the Database default control-plane factory.

The control plane accepts `database.add-database` and `database.add-principal`. Typed descriptor verbs `AddDatabase(name, engine)` and `AddPrincipal(database, name)` record those declarations; an explicit engine gives the database an `engine/database` ownership key. `RemoteReferenceDatabase` supplies the same typed surface for manifest-backed externals. Principal creation receives a named rejection until the runtime supports principal mutation.
Database names cannot contain `/`, which keeps ownership keys unambiguous.

`engine` is the name the database program gives its engine, the model verb's first argument (`builder.AddSql("orders-sql", …)`); omitted, the target's sole engine is used. The command creates an empty database. A database with a schema is declared on the engine builder in the program (`sql.AddDatabase("sales", database => database.Schema(...))`), exists once the engine is built, and belongs to that declaration, so a command naming it is rejected (`docs/DESIGN.md`, "Engine-declared and command-declared databases").

## Dependencies

- `Assimalign.Cohesion.ApplicationModel` for manifests, planned resources, and the
  platform-neutral realization-plan IR.
- `Assimalign.Cohesion.Hosting.Resources` for the default control-plane contract and runtime seam.

The project is guarded by COHAM001 and never references Database runtime, gateway, or
platform packages; its built closure holds no other Database assembly. It emits no legacy resource-specific environment variables; runtime
endpoint and mount values flow through the `Hosting.Resources` `ResourceContext`.
