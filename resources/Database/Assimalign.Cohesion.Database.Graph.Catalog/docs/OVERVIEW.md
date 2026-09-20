# Graph catalog

`Assimalign.Cohesion.Database.Graph.Catalog` provides durable discovery and definition metadata
for one logical graph database. It records node labels, relationship types, property keys, and
named node-property indexes. Labels and relationship types carry `DatabaseObjectOwner` and
an optional owning schema.

Open the catalog with `GraphCatalog.Open(storage, coordinator)` after the shared coordinator
has analyzed and scrubbed recovery. Reads take a `TransactionSnapshot`; writes take an active
`ITransactionContext` and join that transaction without committing it. The engine holds the
definition lock and coordinates changes to graph data and physical indexes.

An initial definition can be marked schema-owned to exercise ownership enforcement.
Subsequent alteration, drop, property change, or index change throws
`DatabaseObjectLockedException` naming the definition, schema, and operation. Compiled graph
schema provisioning remains a separate feature.

The implementation depends on the Database root for ownership contracts, Graph.Storage for
the file-set facade, and the shared Storage, Transactions, and Types packages. It has no
reference to the Graph engine, Hosting, or ApplicationModel. Physical B+Trees and their
maintenance belong to Graph.Storage. See [DESIGN.md](DESIGN.md) for the binary format,
MVCC behavior, and division of responsibilities.

