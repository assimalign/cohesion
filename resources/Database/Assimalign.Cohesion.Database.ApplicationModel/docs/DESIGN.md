# Assimalign.Cohesion.Database.ApplicationModel — Design

## Intent

`libraries/ApplicationModel/DESIGN.md` defines the two-plane split: resources never know they are orchestrated; orchestrators never reference resource runtimes. This project is the database's Layer-3d manifest — the only thing an orchestrator app needs to place a database in its graph.

## Decisions

- **Artifact name `Assimalign.Cohesion.Database.Application`.** The host project is still named `Database.Hosting`; the artifact carries the *future* `.Application` name deliberately, matching the planned repo-wide `.Hosting` → `.Application` rename (ApplicationModel Phase 3) so the manifest doesn't churn when the rename lands.
- **Declared port defaults to 0** (platform-allocated). Dependents resolve the real port through the gateway's observed view; a fixed default port would invite collisions in multi-database graphs. `EndpointScheme` is `cohesion-db` — the wire protocol, not HTTP.
- **One volume mount (`data`)** for the storage files. Configuration/secret mounts get added when the host's configuration binding lands; the record-based `ResourceMount` makes that additive.
- **Hand-written manifest for now.** The `CohesionResourceManifest` MSBuild codegen (ApplicationModel Phase 5) will eventually generate this shape; the hand-written type intentionally matches what the generator is specified to emit so the swap is mechanical.

## Non-goals

- No controller logic — gateways realize the resource with their existing executable controllers.
- No connection-string builders — client-side concerns belong to `Database.Client`.

## AOT posture

Pure declarative types. Nothing to trim.
