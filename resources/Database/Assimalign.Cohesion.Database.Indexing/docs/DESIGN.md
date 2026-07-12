# Assimalign.Cohesion.Database.Indexing — Design

## Intent

Every model needs ordered lookups: SQL secondary indexes, document indexes, graph adjacency, the KV primary structure. Building four bespoke trees would quadruple the hardest code in the platform, so index structures live in the kernel and models bring only their key extraction.

## Byte-comparable keys

`IndexKey` compares by unsigned lexicographic byte order — the single comparison rule every structure (and every future on-disk page compare) uses. Type order is preserved at *encoding* time: integers are big-endian with the sign bit flipped (`FromInt64`), so numeric order and byte order coincide; composite keys concatenate component encodings. String collation encoding is deliberately **not** defined here — it belongs to the shared type system (`Database.Types`), which owns collation identity; this project stays agnostic to what the bytes mean.

This is the same design center as FoundationDB tuples and MySQL/InnoDB memcmp-able keys: one dumb, fast comparator at the bottom, all type intelligence pushed to encoding.

## Transactional binding

`IIndex` mutations take an `ITransactionContext` — index entries are stamped and become visible under the same MVCC rules as the data they reference. There is no "non-transactional index write" surface; recovery replays index changes from the same WAL as data changes. Unique enforcement happens at insert against the *visible* state (a unique violation with an in-flight competing writer resolves through the lock manager, not the index).

## Entry references are opaque `ulong`s

The index maps keys to entry references the owning storage layer understands (page address, row id, node id). Making the reference generic (`IIndex<TReference>`) would infect every cursor and page layout with a type parameter for zero runtime benefit — models already own both sides of the mapping.

## Relationship to `Database.Storage`

`Database.Storage` provides the *physical* substrate through `IStoragePageManager` — index pages (`PageType.Index`) are allocated, pinned, and flushed like any other page and live in the same storage files. This project is the *logical* layer: structures, keys, cursors, uniqueness. The B+Tree implementation binds the two. (An earlier string-based `IStorageIndexManager` stub in `Database.Storage` was removed during the #157 alignment — it duplicated this project's `IIndexManager` at the wrong layer with no design behind it.)

## Non-goals

- No full-text or spatial indexes in the MVP surface (future `IndexKind` members; the enum is the extension point).
- No online index rebuild in the contract yet — DDL-blocking builds first.
- No cost/statistics surface here — planners get statistics through their model catalogs.

## AOT posture

Pure contracts and span-based value objects. No reflection.
