# Graph storage API

`GraphStorage.Create` and `Open` own three streams and expose the shared `Storage`
surface, `WriteAheadJournal`, and `Records` for coordinator construction. Their raw
owner-zero record methods support Graph.Catalog. `PackLocation` and `UnpackLocation`
convert the stable page/slot reference format.

`GraphStore.Open(GraphStorage, TransactionCoordinator)` returns the sealed `GraphStore`, whose
operations create/find/delete nodes and relationships, enumerate nodes and incident
relationships, create/drop/search/list exact property indexes, and recover index writers.
Mutations require an active caller transaction and do not commit it. Snapshot reads
return immutable `StoredGraphNode`, `StoredGraphRelationship` and `StoredGraphIndex`
records.

`GraphStore.EnsureIndexFormat(GraphStorage)` checks, without opening the store or writing
anything, that every registered index tree is in the B-tree page format this engine reads,
and throws `IndexFormatException` (`COHDBI001`) when one is not (#1194). An engine calls it
before its recovery scrub; `Open` checks again as it attaches the trees.

Write conflicts surface as `TransactionAbortedException`; invalid names and properties use
argument exceptions; an element whose record exceeds 8,092 bytes, or whose indexed value
exceeds the 1,016-byte index key, throws `GraphElementTooLargeException` (a
`StorageException`) before anything is written; missing endpoints, connected-node
restrictions and invalid index operations use `InvalidOperationException`. The Graph root
translates these to its session diagnostics (`COHDBG009` for an element too large). Storage integrity failures retain the kernel's
storage exception vocabulary. See [design](../../DESIGN.md) for the full lifecycle
and on-disk format.
