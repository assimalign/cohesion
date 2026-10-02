# Graph storage API

`GraphStorage.Create` and `Open` own three streams and expose the shared `IStorage`
surface, `WriteAheadJournal`, and `Records` for coordinator construction. Their raw
owner-zero record methods support Graph.Catalog. `PackLocation` and `UnpackLocation`
convert the stable page/slot reference format.

`GraphStore.Open(GraphStorage, TransactionCoordinator)` returns `IGraphStore`, whose
operations create/find/delete nodes and relationships, enumerate nodes and incident
relationships, create/drop/search/list exact property indexes, and recover index writers.
Mutations require an active caller transaction and do not commit it. Snapshot reads
return immutable `StoredGraphNode`, `StoredGraphRelationship` and `StoredGraphIndex`
records.

Write conflicts surface as `TransactionAbortedException`; invalid names and properties use
argument exceptions; an element whose record exceeds 8,092 bytes, or whose indexed value
exceeds the 1,016-byte index key, throws `GraphElementTooLargeException` (a
`StorageException`) before anything is written; missing endpoints, connected-node
restrictions and invalid index operations use `InvalidOperationException`. The Graph root
translates these to its session diagnostics (`COHDBG008` for an element too large). Storage integrity failures retain the kernel's
storage exception vocabulary. See [design](../../DESIGN.md) for the full lifecycle
and on-disk format.
