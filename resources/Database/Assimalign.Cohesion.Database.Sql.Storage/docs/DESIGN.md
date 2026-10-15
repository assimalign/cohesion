# Assimalign.Cohesion.Database.Sql.Storage — Design

The SQL model's *storage layout* project (area architecture:
[resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md) §3.3 — every model brings a layout,
never its own paging/WAL). This library is deliberately thin: `SqlStorage` derives
from the kernel's `Storage` base and re-exposes the protected record operations as a
public row-oriented surface. All hard invariants (write-ahead ordering, recovery
replay, buffer-pool behavior, per-owner record chains) live in — and are documented
by — `Assimalign.Cohesion.Database.Storage`.

## Why-this-not-that

- **A facade, not a layout engine.** SQL rows are "variable-length byte sequences
  addressed by (page, slot)" — exactly the substrate's slotted-page unit — so the
  SQL layout adds no page format of its own. The project still exists (rather than
  the engine using `Storage` directly) because the base class's record operations
  are `protected` by design: models opt into the record surface they actually
  expose, and `SqlStorage` is where the SQL model does that, plus where a
  SQL-specific layout would grow if one ever became necessary.
- **Row placement is owner-chained.** `InsertRow(transaction, ownerId, row)` passes
  the owning table's object id through to the substrate's per-owner record chains,
  giving each table page locality: table scans (`GetUnitIterator(objectId)`) touch
  only that table's pages. `DROP TABLE` durably retires the identity in the catalog;
  SQL's version-purge worker releases at most 32 owned data/index pages per batch after
  Build and the old-reader horizon. The ownerless
  overloads write the shared (owner-zero) space — the catalog file set's layout,
  where kind-discriminated metadata records are few and scanned as a whole.
  Reclamation intents (catalog kind 14, SQL format 11) instead use a private page owned
  by the retired table id, so completing cleanup never edits live metadata. The existing
  transactional `FreeOwnerPages` accepts a positive batch bound; recovered page headers
  rebuild both directories and the FSM, making repeated batches crash-safe. Data frees
  are durable before the intent is removed from the separate catalog file set. No SQL
  storage layout/header version changes; the SQL engine's format gate moved from 10 to 11.
- **The journal is a public composition seam.** `WriteAheadJournal` returns the
  storage's `StorageJournal`, matching Documents, Graph, and Blob storage.
  It replaces the shipped-to-shipped friend grant and hands the
  engine's transaction coordinator the same journal the storage brackets write page
  images to, for the manager's journal-bound transaction log and open-time recovery
  analysis. `Open(..., checkpointOnOpen: false)` exists for the same reason — the
  engine analyzes the recovered journal before the truncating checkpoint destroys
  the records classification reads (see the Sql model's internal `SqlStorageStrategy`). Storage retains
  ownership of the journal; composers coordinate journal writes and checkpoints
  through their transaction coordinator and must not dispose it independently.

## Error model

The durability-aware `SqlStorage.Open` accepts a cancellation token (49a), passed to physical
recovery. Journal initialization and all replay passes, plus the page-header scan, observe it.
A canceled open disposes its streams and keeps its journal, so replay is repeatable. Logical
scrub cancellation belongs to the SQL engine and transaction coordinator; SQL fences an
interrupted scrub before closing so shutdown cannot truncate unanalyzed lifecycle records.

No exception types of its own: everything is the substrate's `StorageException`
family. The SQL engine translates at its model boundary per the area error policy.

## AOT posture

Inherits the substrate's posture (explicit-layout header overlays, span-based
codecs); this project adds no reflection and no serialization.

## Non-goals

- No SQL semantics (types, visibility, catalogs) — those live in `Database.Sql`,
  `Database.Sql.Catalog`, and the row codec inside the engine.
- No bespoke page formats until a measured need exists; the slotted layout is the
  layout.
