# Assimalign.Cohesion.Database.Indexing — Overview

Shared index infrastructure for every Cohesion database engine: order-preserving byte-comparable keys, index and cursor contracts, and per-database index management. B+Tree is the first physical structure; hash indexes follow post-MVP.

## Scope

- `IndexKey` / `IndexKeyRange` — byte-comparable key encoding (implemented, tested) and range scans
- `IIndex` — point/range operations bound to a transaction context
- `IIndexCursor` — streaming scan iteration
- `IIndexManager` / `IndexDefinition` — create/drop/open per logical database
- `IndexKind` — BTree (default), Hash (post-MVP)

## Dependencies

- `Assimalign.Cohesion.Database.Storage` (pages the structures are built on)
- `Assimalign.Cohesion.Database.Transactions` (mutations ride the owning transaction)

This package is a **child root** of the Database area: the area root
(`Assimalign.Cohesion.Database`) rolls it up — never the reverse — so the index
infrastructure stays independently consumable. Index errors raise the package's
own `IndexException` root (inherits `Exception`, not `DatabaseException`); model
engines translate at their boundary.

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
