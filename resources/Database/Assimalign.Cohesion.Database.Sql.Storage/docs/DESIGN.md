# Assimalign.Cohesion.Database.Sql.Storage — Design

The SQL model's *storage layout* project (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md) §3.3 — every model brings a layout,
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
  only that table's pages, and `DROP TABLE` releases the chain. The ownerless
  overloads write the shared (owner-zero) space — the catalog file set's layout,
  where kind-discriminated metadata records are few and scanned as a whole.
- **The journal is surfaced, narrowly.** `WriteAheadJournal` (internal) hands the
  engine's transaction coordinator the same journal the storage brackets write page
  images to, for the manager's journal-bound transaction log and open-time recovery
  analysis. `Open(..., checkpointOnOpen: false)` exists for the same reason — the
  engine analyzes the recovered journal before the truncating checkpoint destroys
  the records classification reads (see `ISqlStorageStrategy`).

## Error model

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
