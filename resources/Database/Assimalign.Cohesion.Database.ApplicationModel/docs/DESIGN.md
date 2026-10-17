# Assimalign.Cohesion.Database.ApplicationModel — Design

## Intent

This project is the Database area's AOT-compatible, dependency-guarded orchestration package. It gives a
gateway a typed database resource, a platform-neutral realization planner, and the
default control-plane factory that every orchestration-enabled database executable
registers. It never references `Database.Hosting`, an engine package, a gateway
implementation, or a platform object model.

An enabled `Sdk.Database` executable produces `cohesion/resource/v1` at build time.
`Sdk.Gateway` generates a typed `Manifests.<Name>` for it, and the gateway's `Program.cs`
passes that `ResourceManifest` to this package's hand-written `AddDatabase(...)` verb (the
SDK generates no per-resource verb); the ApplicationModel package does not reconstruct
endpoint, mount, artifact, or lifecycle facts from conventions.

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
values flow through the frozen `Hosting.Resources` `ResourceContext` contract instead.

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

IDatabaseResourceDescriptor database = builder.AddDatabase(
    manifest,
    new DatabaseResourceOptions
    {
        Replicas = 2,
        Storage = { Size = "20Gi" },
    });
```

A gateway passes the generated `Manifests.<Name>` and, optionally, the same typed options
(`builder.AddDatabase(Manifests.OrdersDatabase, new DatabaseResourceOptions { ... })`);
application authors normally do not load the JSON themselves.

## Default control plane

`DatabaseResourceControlPlane.Create()` returns a fresh `Hosting.Resources`
`IResourceControlPlane`. The enabled resource's generated
`ResourceControlPlane.g.cs` registers that factory with the `Hosting.Resources`
`ResourceRuntime` and seeds it
with the invocation's observed endpoints. `Database.Hosting` reads the registration
through the shared `Hosting.Resources` contract and serves it on the manifest's `admin` endpoint;
neither package references the other.

The accepted kinds are `database.add-database` and `database.add-principal`. The typed
descriptor verbs record these declarations; Hosting owns their mutation handlers.

## Engine-declared and command-declared databases

A database executable owns databases in two ways, and this package sees only the second:

- **Declared by the engine's builder** in the target's `Program.cs`
  (`builder.AddSql("orders-sql", sql => sql.AddDatabase("sales", database => database.Schema(...)))`).
  The engine opens or creates each one, and a SQL engine provisions its schema, before the
  engine's build returns, so it exists before the control plane accepts a command. The
  declaration owns it: the engine refuses to drop it (owner decision 56 of 2026-10-09).
- **Declared by a gateway command**, `descriptor.AddDatabase(name, engine)`. Hosting's handler
  creates an empty database on the running engine (no schema; a SQL database gets the binary
  default collation) and records it under the command's key; deleting the command drops it.

The two never share a database. A command that names a database the engine already holds,
an engine-declared one included, is rejected (`cannot claim existing database`), because
this declaration did not create it; names compare as the engine compares them, ignoring
case. The design of record keeps this refusal unchanged
(`docs/programs/DATABASE_ENGINE_EXTENSIBILITY_DESIGN.md` §5.6), and
`ResourceCommandHostingTests` in Database.Hosting pins it for a builder-declared database.
A database whose schema is code therefore belongs on the engine builder; the command is for
an empty database the deployment, not the program, decides to add.

The `engine` field is the engine's name as the target registers it: the first argument of
its model verb (`AddSql("orders-sql", …)`), written once since the builder redesign (owner
decision 52). Omitted, it selects the target's sole engine; with two or more engines the
handler rejects the command and asks for a name.

## Dependency and AOT posture

COHAM001 constrains the complete production dependency closure to
`Assimalign.Cohesion.Core`, `Assimalign.Cohesion.ApplicationModel`,
`Assimalign.Cohesion.Hosting`, `Assimalign.Cohesion.Hosting.Health`,
`Assimalign.Cohesion.Hosting.Resources`, and the permitted BCL surface. The planner uses typed
records and ordinary loops only. Golden serialization goes through
`ResourcePlanJsonContext`; there is no reflection, assembly scanning, runtime code
generation, runtime database dependency, or platform SDK dependency.

The concrete-first program (`database-area.md`) and the engine builder redesign left this
package unchanged, which phase 7 of the concrete-types plan verified: its built closure is
exactly the list above plus `System.Security.Cryptography.ProtectedData`, with no other
Database assembly (no root, model or engine), and `IDatabaseResourceDescriptor` is one of the five
interfaces the area keeps. It stays an interface because it extends the library-owned
`IResourceCommandDescriptor`, the pattern of every area's `I<Area>ResourceDescriptor`;
`database-area.md` keeps it as one of its five interfaces because it is orchestration, not an
engine model, and no other public interface may be added to this package.

## Non-goals

- Hosting engines, protocol servers, health endpoints, or database provisioning.
- Inventing a manifest from a resource name or an executable-name convention.
- Carrying platform-specific scheduling, storage-class, service, or claim types.
- Connection settings or client factories; those belong to `Database.Client`.
- Expanding runtime mutation capabilities or introducing engine dependencies into this package.

## Typed descriptors and commands

`AddDatabase` returns `IDatabaseResourceDescriptor`. `RemoteReferenceDatabase(declaration,
configure)` returns the same typed surface for a manifest-backed external. Both preserve the
canonical graph resource identity, dependency edges, and command accumulation across rebinding.

`descriptor.AddDatabase(name, engine: null, optional: false)` records `database.add-database`
with `{ "database": name, "engine": engine }`; an omitted engine field selects the sole runtime
engine. `AddPrincipal(database, name, optional: false)` records `database.add-principal` with
`{ "database": database, "name": name }`. The database conflict key is `engine/database` when an engine is supplied, otherwise the database name; the principal key is
`database/name`. Database names cannot contain `/`, so engine-prefixed keys remain unambiguous;
engine names may contain `/`. There are no credential or grant payload fields. A provider
without a principal-mutation capability returns a named rejection.

Source-generated JSON metadata produces canonical payload bytes through ApplicationModel.
`Build()` validates accepted kinds and graph ownership; no verb contacts the database.
