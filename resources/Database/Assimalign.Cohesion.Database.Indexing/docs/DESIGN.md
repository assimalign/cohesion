# Assimalign.Cohesion.Database.Indexing — Design

## Intent

Every model needs ordered lookups: SQL secondary indexes, document indexes, graph adjacency, the KV primary structure. Building four bespoke trees would quadruple the hardest code in the platform, so index structures live in the kernel and models bring only their key extraction. The SQL engine is the first real consumer (#912): CREATE/DROP INDEX, write-path maintenance, and planner seeks all compose this package's manager, trees, and cursors.

## Byte-comparable keys

`IndexKey` compares by unsigned lexicographic byte order — the single comparison rule every structure (and every future on-disk page compare) uses. Type order is preserved at *encoding* time: integers are big-endian with the sign bit flipped (`FromInt64`), so numeric order and byte order coincide; composite keys concatenate component encodings. String collation encoding is deliberately **not** defined here — it belongs to the shared type system (`Database.Types`), which owns collation identity; `IndexKey.From(DatabaseKeyWriter)` is the bridge for typed and composite keys.

This is the same design center as FoundationDB tuples and MySQL/InnoDB memcmp-able keys: one dumb, fast comparator at the bottom, all type intelligence pushed to encoding.

## The B+Tree implementation

`BTreeIndexManager.Create(options)` composes B+Trees over `PageType.Index` pages —
one node per page, a sorted offset directory with entry data growing from the body
end, key capacity capped (`MaxKeyLength` = 1 KiB) so a node always holds several
entries and splits stay correct.

- **Every page mutation rides the owning storage transaction** through the storage
  layer's `OpenPageForWrite`/`AllocatePageForWrite` — before-images at first touch,
  after-images at commit. That is the whole crash story: a crash mid-split reverts
  to the consistent pre-transaction tree; committed splits replay from the journal
  (the crash suites prove both). `IStorageTransactionSource` is how the engine
  pairs logical transaction contexts with their storage transactions.
- **MVCC entries, tombstone deletes.** Leaf entries carry writer and deleter
  sequence stamps; reads filter through the caller's snapshot (writer visible,
  deleter absent-or-invisible). Deletes stamp — never remove — so old snapshots
  keep seeing the entry; an aborted deleter's stamp reverts physically with its
  page image. Physical reclamation and node merges belong to vacuum, which follows
  version pruning (post-MVP).
- **Uniqueness checks the LATEST state, not the snapshot.** Two transactions that
  began before each other's commits would both pass a snapshot-visibility check
  (write skew). Instead: unique inserts *and deletes* first take an exclusive
  key lock (hashed key) in the shared lock manager; once held, a live entry
  (deleter stamp zero) can only be committed or our own — uncommitted others are
  excluded by the lock, and aborted writers' entries were physically reverted.
- **Concurrency (MVP): a tree-level reader/writer latch.** Writers exclusive;
  cursors materialize their range's visible entries under the read latch, so no
  latch is held across awaits and readers never see a torn structure. Lock
  coupling / latch-per-node is a measured-need follow-up.
- **Directory persistence belongs to the catalog.** The manager keeps an in-memory
  directory and exports `BTreeIndexRegistration`s (`IIndexRegistry`); the model
  catalog persists them and re-attaches on open (`ExistingIndexes`). Root splits
  change the root page id — catalogs re-export at their persistence points.
  Dropping an index is a directory operation; its pages await vacuum.

## Transactional binding

`IIndex` mutations take an `ITransactionContext` — index entries are stamped and become visible under the same MVCC rules as the data they reference. There is no "non-transactional index write" surface; recovery replays index changes from the same WAL as data changes. Unique enforcement happens at insert against the *visible* state (a unique violation with an in-flight competing writer resolves through the lock manager, not the index).

### The maintenance surfaces (model-engine consumers)

The SQL engine's index adoption (#912) added a small family of operations that
deliberately take the **physical bracket** (`IStorageTransaction`) instead of a
transaction context — they run where no statement bracket exists:

- **`InsertVersionAsync(bracket, key, reference, writer, deleter)`** — the
  offline (DDL-blocking) build path: an index built over existing rows inserts
  each stored version with its original stamps, so pre-existing snapshots read
  through the new index exactly what the row scan shows them. No uniqueness
  check — the builder detects live duplicates itself under the object's
  exclusive lock (online rebuild remains a non-goal).
- **`EraseAsync` / `ClearDeleterAsync`** — the logical-undo pair: physically
  remove an aborted writer's insert; clear an aborted writer's tombstone. Both
  verify the recorded stamp before acting, so replays and stale ledgers no-op.
  Physical removal drops only the directory slot; the entry bytes stay orphaned
  in the node until a split rebuilds it (bounded space for a rare path).
- **`IIndexManager.PurgeWritersAsync(bracket, writers)`** — the open-time
  recovery obligation: one walk per tree removes every unproven writer's
  entries and clears their tombstones (the in-memory undo ledger died with the
  process; snapshots have no commit-log awareness, so unproven stamps must not
  serve reads). Idempotent across the crash window.
- **`OpenCursor(TransactionSnapshot, range)`** — reads through an explicit
  snapshot, so a statement-scoped reader (per-statement snapshots under
  ReadCommitted) sees exactly the same visibility through the index as through
  its row scan.
- **`IndexKey.Hash()`** — publishes the FNV-1a key-lock identity so a writer
  that must never wait inside a serialized apply scope can pre-acquire the
  unique-key lock in its own lock phase and rely on the lock manager's
  same-owner re-grant when the tree acquires it again internally.

## Entry references are opaque `ulong`s

The index maps keys to entry references the owning storage layer understands (page address, row id, node id). Making the reference generic (`IIndex<TReference>`) would infect every cursor and page layout with a type parameter for zero runtime benefit — models already own both sides of the mapping.

## Error model

`IndexException` is the package's own exception root and inherits `Exception`
directly, not the area's `DatabaseException` — this package is a child root the
area root rolls up (2026-07-13 inversion), so it must stay independently
consumable. `IndexUniqueViolationException : IndexException` is the typed
unique-violation surface; model engines that expose index failures on the area's
error surface translate at their own boundary.

## Relationship to `Database.Storage`

`Database.Storage` provides the *physical* substrate through `IStoragePageManager` — index pages (`PageType.Index`) are allocated, pinned, and flushed like any other page and live in the same storage files. This project is the *logical* layer: structures, keys, cursors, uniqueness. The B+Tree implementation binds the two. (An earlier string-based `IStorageIndexManager` stub in `Database.Storage` was removed during the #157 alignment — it duplicated this project's `IIndexManager` at the wrong layer with no design behind it.)

## Non-goals

- No full-text or spatial indexes in the MVP surface (future `IndexKind` members; the enum is the extension point).
- No online index rebuild in the contract yet — DDL-blocking builds first.
- No cost/statistics surface here — planners get statistics through their model catalogs.

## AOT posture

Pure contracts and span-based value objects. No reflection.
