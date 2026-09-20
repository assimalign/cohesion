# Assimalign.Cohesion.Database.Storage — Overview

The physical storage kernel of the Cohesion Data Platform: fixed-size pages, a buffer
pool with pin-counted caching, slotted-page record layout, a free-space map, and the
journal (write-ahead log) that provides durability. Every database model (SQL,
Documents, Graph, Blob, KeyValuePair) composes this project for its on-disk
representation — model-specific layouts live in `{Model}.Storage` projects, never here.

## Scope

- **Pages** — 8 KiB `Page` unit with a 96-byte header (id, LSN, CRC-32 checksum, type,
  flags, slot bookkeeping), `SlottedPage` variable-length record layout, `PageSlot`
  directory entries.
- **Buffer pool** — `IStorageBufferPool` pin/unpin caching over a `StorageStream`;
  checksum stamped on write-back, verified on load.
- **Page management** — `IStoragePageManager` allocation/free/retrieval/flush;
  `IStorageFreeSpaceMap` allocation tracking, rebuilt from page headers on open.
- **Records** — `Storage` abstract base with insert/read/update/delete over slotted
  pages and `IStorageUnitIterator` full scans.
- **Journal** — `IStorageJournal` write-ahead logging with begin/commit/rollback
  markers, CRC-protected frames, and recovery replay of committed operations.
- **File set** — each storage instance owns three streams: data (`.dat`), journal
  (`.log`), and backup (`.bak`), wrapped by `StorageStream`.

## Dependencies

None — this is a leaf kernel project. Consumers: `Database.Transactions` (WAL binding),
`Database.Indexing` (index pages), every `{Model}.Storage` project.

## Usage

Models derive a thin facade from `Storage` (see `Assimalign.Cohesion.Database.Sql.Storage`
for the canonical example):

```csharp
public sealed class SqlStorage : Storage
{
    public override StorageModel Model => StorageModel.Sql;
    // static Create(...)/Open(...) factories call InitializeNew/OpenExisting
}
```

See [DESIGN.md](DESIGN.md) for the architecture and the decisions behind it.

## Large streamed records

Model packages can chain records larger than a page while retaining the shared
kernel. Deleting a record releases its page transactionally once its last live
slot disappears. `StorageJournal.ReadSequential` permits startup recovery without
materializing WAL page payloads; `ReadAll` remains available for callers that need
a materialized snapshot. See [DESIGN.md](DESIGN.md) for replay and reclamation.
