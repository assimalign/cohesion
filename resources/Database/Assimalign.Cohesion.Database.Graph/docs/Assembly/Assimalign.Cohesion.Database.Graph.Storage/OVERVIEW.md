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
and throws `IndexFormatException` (`CK06101`) when one is not (#1194). An engine calls it
before its recovery scrub; `Open` checks again as it attaches the trees.

Write conflicts surface as `TransactionAbortedException`; invalid names and properties use
argument exceptions; an element whose record exceeds 8,092 bytes, or whose indexed value
exceeds the 1,016-byte index key, throws `GraphElementTooLargeException` (a
`StorageException`) before anything is written; missing endpoints, connected-node
restrictions and invalid index operations use `InvalidOperationException`. The Graph root
translates these to its session diagnostics (`CG02009` for an element too large). Storage integrity failures retain the kernel's
storage exception vocabulary. See [design](../../DESIGN.md) for the full lifecycle
and on-disk format.

`UpdateNodeAsync(id, labels, properties, context, cancellationToken)` and `UpdateRelationshipAsync(id, properties, context, cancellationToken)` return a replacement MVCC version even for identical supplied values, or null for an absent identity. GQL filters pure no-op SET assignments before calling storage. Node updates replace the complete labels and properties; relationship updates preserve type and endpoints. Every index and adjacency entry moves to the new physical location. `EnsureIndexFormat` also refuses graph record formats other than 4 before logical recovery scrub writes.

CreateRelationshipAsync(relationship, typeId, context, cancellationToken) requires the relationship
type's nonempty catalog GUID. The store persists it and preserves it through property updates.
The filtered GetIncidentAsync(nodeId, direction, typeIds, snapshot, cancellationToken) streams
selected ranges; GraphIncidentDirection chooses Outgoing, Incoming or Both. A null type selection
reads all types, an empty selection reads none, and duplicate IDs do not duplicate work. Self-loops
appear once under Both. The caller retains the snapshot while enumerating and disposes the
enumerator on early exit. Cross-range order is unspecified; the existing unfiltered list overload
continues to return identity order. Version-3 registrations are also checked before recovery.
