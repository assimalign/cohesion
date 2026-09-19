# App.Database runtime framework design

The Database framework inventory is declared in the App.Database item group of
[Assimalign.Cohesion.App.props](../../Assimalign.Cohesion.App.props). Both runtime and
reference packs consume this inventory, so an SDK consumer receives the public Database
family without adding model package references by hand.

The inventory includes `Assimalign.Cohesion.Database.Sql.Schema` beside the SQL engine
and catalog. This package carries SQL declarations, canonical compiled schemas, validation,
and migration planning. The area root retains model-independent compiled-schema identity,
ownership, and provisioning contracts. Build-time Database SDK tasks reference the schema
package directly and do not load the SQL engine or its model storage and TCP server dependencies.

`Assimalign.Cohesion.Database.Mapping` delivers model-independent identity, snapshot tracking and
atomic save coordination through both packs. The reference pack also bundles the existing
`Assimalign.Cohesion.SourceGeneration.Database` generator under `analyzers/dotnet/cs/` and lists it
as an analyzer in `FrameworkList.xml`; it does not enter the runtime pack. `Sdk.Database` exposes
the `CohesionGenerateDatabaseMappers` compiler property, and consumers enable generation explicitly
by setting it to `true`. Shipping the analyzer therefore does not impose mapping diagnostics on
existing schema-only applications. Validate analyzer delivery by packing and inspecting the
reference pack as well as the runtime pack.

Every shipped inventory member is also listed in the Database CI matrix and the installer
release inventory. Validate assembly closure and package contents by packing this runtime
project after inventory changes. Model schema extraction changes delivery only; host
composition and runtime service ordering are unchanged.
