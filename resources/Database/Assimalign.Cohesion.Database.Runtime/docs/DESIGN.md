# App.Database runtime framework design

The Database framework inventory is declared in this producer's
[Directory.Build.props](../Directory.Build.props). The sibling Refs producer imports that file,
so both runtime and reference packs consume this inventory, and an SDK consumer receives the
public Database family without adding model package references by hand.

The inventory includes `Assimalign.Cohesion.Database.Sql.Schema` beside the SQL engine
and catalog. This package carries SQL declarations, canonical compiled schemas, validation,
and migration planning. The area root retains model-independent compiled-schema identity,
ownership, and provisioning contracts. Build-time Database SDK tasks reference the schema
package directly and do not load the SQL engine or its model storage and TCP server dependencies.

Every shipped inventory member is also listed in the Database CI matrix and the installer
release inventory. Validate assembly closure and package contents by packing this runtime
project after inventory changes. Model schema extraction changes delivery only; host
composition and runtime service ordering are unchanged.
