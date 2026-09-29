# Graph storage API

`GraphStorage.Create` and `Open` own three streams and expose the shared `IStorage`
surface, `WriteAheadJournal`, and `Records` for coordinator construction. Their raw
owner-zero record methods support Graph.Catalog. `PackLocation` and `UnpackLocation`
convert the stable page/slot reference format.

`GraphStore.Open(GraphStorage, TransactionCoordinator)` returns `IGraphStore`, whose
operations create/find/delete nodes and relationships, enumerate nodes and incident
relationships, create/drop/search exact property indexes, and recover index writers.
Mutations require an active caller transaction and do not commit it. Snapshot reads
return immutable `StoredGraphNode` and `StoredGraphRelationship` scalar records.

Write conflicts surface as `TransactionAbortedException`; invalid properties and record
limits use argument exceptions; missing endpoints, connected-node restrictions and
invalid index operations use `InvalidOperationException`. The Graph root translates
these to its session diagnostics. Storage integrity failures retain the kernel's
storage exception vocabulary. See [design](../../DESIGN.md) for the full lifecycle
and on-disk format.
