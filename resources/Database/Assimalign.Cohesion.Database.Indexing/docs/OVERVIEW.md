# Assimalign.Cohesion.Database.Indexing — Overview

Shared index infrastructure for every Cohesion database engine: order-preserving byte-comparable keys, index and cursor contracts, and per-database index management. B+Tree is the first physical structure; hash indexes follow post-MVP.

## Scope

- `IndexKey` / `IndexKeyRange` — byte-comparable key encoding (implemented, tested) and range scans
- `BTreeIndex` — point/range operations bound to a transaction context (sealed)
- `BTreeCursor` — incremental forward/reverse scans with bounded owned batches and cancellation (sealed)
- `BTreeIndexManager` / `IndexDefinition` — create/drop/open per logical database, and the
  registrations a catalog persists (sealed; `BTreeIndexManager.Create`)
- `IndexKind` — BTree (default), Hash (post-MVP)

## Dependencies

- `Assimalign.Cohesion.Database.Storage` (pages the structures are built on)
- `Assimalign.Cohesion.Database.Transactions` (mutations ride the owning transaction)

This package is a **child root** of the Database area: the area root
(`Assimalign.Cohesion.Database`) rolls it up — never the reverse — so the index
infrastructure stays independently consumable. Index errors raise the package's
own `IndexException` root (inherits `Exception`, not `DatabaseException`); model
engines translate at their boundary.

The package owns the B-tree page format (`BTreeIndexManager.FormatVersion`, format
2 since #1194: entries ordered by key, entry reference and writer). Attaching a
tree written in any other format fails with `IndexFormatException` (`COHDBI001`);
each model's open surfaces that refusal — see [DESIGN.md](DESIGN.md), "Page format
2 and the format gate". An attached tree that reaches a damaged page fails the
operation with `IndexCorruptionException` (`COHDBI002`).

## Consumers

SQL secondary indexes, document indexes, graph adjacency lookups, and the KeyValuePair primary structure are all built on these contracts.

**The SQL engine is the first live consumer** (`Assimalign.Cohesion.Database.Sql`, #912):
`CREATE [UNIQUE] INDEX` / `DROP INDEX` compose `BTreeIndexManager` over each
database's data file set, INSERT/UPDATE/DELETE maintain entries with the same
MVCC stamps as the row versions they reference, the planner drives seek cursors
with statement snapshots, and `Sql.Catalog` persists the manager's registrations
(plus the schema-level index descriptions) for re-attachment on open. The
maintenance surfaces this consumer added — the stamp-preserving build path, the
physical undo pair, the aborted-writer purge walk, and the snapshot cursor
overload — are documented in [DESIGN.md](DESIGN.md).

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

## Dead-version maintenance (#1195, #1239)

`BTreeIndexManager.PruneAsync(bound, token)` removes safe committed tombstones in batches of
at most 64 leaf pages, preserving old snapshots and resuming after cancellation or its budget.
The owning coordinator calls it under the apply gate. Unique inserts use the optional
`BTreeIndexManagerOptions.PruneBound` provider to prune their key's history while reusing one
descent and insertion position through splits. Read cursors stay read-only. Empty-leaf reclamation
and merges remain deferred; B-tree format 2 is unchanged. Physical page rollback remains the
approved #1393 design; [DESIGN.md](DESIGN.md) records the alternatives and measurements.
