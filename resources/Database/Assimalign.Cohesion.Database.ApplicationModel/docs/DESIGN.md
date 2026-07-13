# Assimalign.Cohesion.Database.ApplicationModel — Design

## Intent

`libraries/ApplicationModel/DESIGN.md` defines the two-plane split: resources never know they are orchestrated; orchestrators never reference resource runtimes. This project is the database's Layer-3d manifest — the only thing an orchestrator app needs to place a database in its graph.

## Decisions

- **Artifact name `Assimalign.Cohesion.Database.Application`.** The host project is still named `Database.Hosting`; the artifact carries the *future* `.Application` name deliberately, matching the planned repo-wide `.Hosting` → `.Application` rename (ApplicationModel Phase 3) so the manifest doesn't churn when the rename lands.
- **Declared port defaults to 0** (platform-allocated). Dependents resolve the real port through the gateway's observed view; a fixed default port would invite collisions in multi-database graphs. `EndpointScheme` is `cohesion-db` — the wire protocol, not HTTP.
- **One volume mount (`data`)** for the storage files. Configuration/secret mounts get added when the host's configuration binding lands; the record-based `ResourceMount` makes that additive.
- **Conventional environment variables bridge the two planes.** The resource injects
  `COHESION_DATABASE_DATA_PATH`, `COHESION_DATABASE_ENDPOINT_PORT`, and
  `COHESION_DATABASE_DURABILITY` (exposed as constants on `DatabaseResource`). These are
  the *same names* `Database.Hosting`'s `DatabaseHostConfiguration` binds — the manifest
  and the host agree by convention because they share no assembly (the manifest never
  references the runtime). The data path always maps to the mount path; the **port is
  set only when explicitly declared** — a platform-allocated port (the default 0) is
  observed after launch and injected by the gateway, so hardcoding it here would fight
  the observed view; durability is set only when configured. Caller-supplied
  `EnvironmentVariables` win on a name clash.
- **Hand-written manifest for now.** The `CohesionResourceManifest` MSBuild codegen (ApplicationModel Phase 5) will eventually generate this shape; the hand-written type intentionally matches what the generator is specified to emit so the swap is mechanical.

## Realization behavior (integration-tested)

The manifest is exercised through the generic `ApplicationGateway` realization algorithm
(which `LocalGateway` inherits): `AddDatabase(...)` with a dependent that `DependsOn` it
realizes the database **first**, gates the dependent on the database reaching `Running`,
publishes the database's **observed endpoint** (the concrete allocated port) into the
state manager for the dependent to consume, tears down in **reverse order**, and marks
dependents **`Blocked`** if the database fails to become ready. These are covered by
`DatabaseOrchestrationTests` over a recording controller + state manager.

A full `LocalGateway` end-to-end that *launches the real host process* is deferred: it
needs the packaged `Assimalign.Cohesion.Database.Application` executable artifact on disk
(the local resolver throws `FileNotFoundException` otherwise), which is host-packaging
work outside a manifest unit test. The generic-algorithm coverage above proves every
gateway-observable behavior the manifest is responsible for.

## Non-goals

- No controller logic — gateways realize the resource with their existing executable controllers.
- No connection-string builders — client-side concerns belong to `Database.Client`.

## AOT posture

Pure declarative types. Nothing to trim.
