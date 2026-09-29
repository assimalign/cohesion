# Graph.Storage

`Assimalign.Cohesion.Database.Graph.Storage` stores property-graph nodes, relationships,
endpoint adjacency and secondary node-property indexes. It targets .NET 10, is AOT
compatible, and uses explicit binary codecs without reflection.

`GraphStorage` owns the data, journal and backup streams and delegates page allocation,
checksums and physical recovery to `Database.Storage`. Construct a `TransactionCoordinator`
from its `WriteAheadJournal` and `Records`, then use `GraphStore.Open` to obtain an
`IGraphStore`. Every mutation takes the caller's `ITransactionContext` and leaves commit
or rollback to that caller. Reads take an immutable `TransactionSnapshot`.

The store exposes creation, lookup, restricted or cascading deletion, incident-edge
lookup, and exact node-property index creation/search/drop. An incident-edge lookup is
a B+Tree range seek and does not scan every relationship. Property index searches return
scalar-verified candidates in identity order. The graph root maps storage DTO identities
to the frozen `GraphNodeId` and `GraphRelationshipId` contracts and plans traversals.

Dependencies are `Database.Storage`, `Database.Transactions` and `Database.Indexing`.
There is no dependency on the Graph root, its catalog, Hosting or ApplicationModel.
Owner-zero pages remain available to `Graph.Catalog` through the low-level record seam.

See [DESIGN.md](DESIGN.md) for the binary format, adjacency costs, numeric index
semantics, transaction lifecycle, recovery sequence and current limits.
