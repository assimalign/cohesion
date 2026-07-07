# Assimalign.Cohesion.Database.Indexing — Overview

Shared index infrastructure for every Cohesion database engine: order-preserving byte-comparable keys, index and cursor contracts, and per-database index management. B+Tree is the first physical structure; hash indexes follow post-MVP.

## Scope

- `IndexKey` / `IndexKeyRange` — byte-comparable key encoding (implemented, tested) and range scans
- `IIndex` — point/range operations bound to a transaction context
- `IIndexCursor` — streaming scan iteration
- `IIndexManager` / `IndexDefinition` — create/drop/open per logical database
- `IndexKind` — BTree (default), Hash (post-MVP)

## Dependencies

- `Assimalign.Cohesion.Database` (contract root)
- `Assimalign.Cohesion.Database.Storage` (pages the structures are built on)
- `Assimalign.Cohesion.Database.Transactions` (mutations ride the owning transaction)

## Consumers

SQL secondary indexes, document indexes, graph adjacency lookups, and the KeyValuePair primary structure are all built on these contracts.
