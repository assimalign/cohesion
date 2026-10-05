# Assimalign.Cohesion.Database.Storage — Overview

The physical storage kernel of the Cohesion Data Platform: fixed-size pages, a buffer
pool with pin-counted caching, slotted-page record layout, a free-space map, and the
journal (write-ahead log) that provides durability. Every database model (SQL,
Documents, Graph, Blob, KeyValuePair) composes this project for its on-disk
representation — model-specific layouts live in `{Model}.Storage` projects, never here.

## Scope

- **Pages** — 8 KiB `Page` unit with a 96-byte header (id, LSN, CRC-32C checksum, type,
  flags, slot bookkeeping), `SlottedPage` variable-length record layout, `PageSlot`
  directory entries.
- **Buffer pool** — `IStorageBufferPool` pin/unpin caching over a `StorageStream`;
  checksum stamped on write-back, verified on load. 4,096 pages (32 MiB) by default,
  resizable through `Storage.BufferPoolCapacity`; engines expose it as an option (#1254).
- **Page management** — `IStoragePageManager` allocation/free/retrieval/flush;
  `IStorageFreeSpaceMap` allocation tracking, rebuilt from page headers on open.
- **Records** — `Storage` abstract base with insert/read/update/delete over slotted
  pages and `IStorageUnitIterator` full scans.
- **Journal** — `IStorageJournal` write-ahead logging with begin/commit/rollback
  markers, CRC-32C-protected frames, and recovery replay of committed operations. Appends go
  to a user-space buffer, frames built in place, which drains to the operating system in one
  write before every commit is acknowledged (every durability mode), every reader, the
  write-ahead gate and a checkpoint's truncation (#1252, PostgreSQL's WAL buffers).
- **Offline on a failed journal write, fsync or header write** — a durable flush of the journal
  or the data file (#1243), a write of the journal's buffer (#1252), or a header slot write
  that fails once issued (#1268) takes the storage offline (`StorageOfflineException`,
  `COHDBS002`): nothing more is written to either file, closing included, until the file set
  is reopened and its recovery
  decides every unconfirmed commit (PostgreSQL's PANIC on a failed WAL fsync).
  `StorageOfflineException.Cause` (`StorageOfflineCause`: `JournalFlush`, `DataFlush`,
  `HeaderWrite`) names what failed, and the first failure is kept for the life of the instance.
  `OnOffline` is raised once when it happens, with that failure, so an engine takes a
  database's other file sets offline in the same moment, and `CommitRecordWritten` marks a
  commit that may survive.
- **Checkpoint triggers** — `CheckpointJournalSize` asks for a checkpoint when the journal
  reaches a size (`OnCheckpointNeeded`), and `IsCheckpointDue(interval)` adds a time backstop
  that skips idle journals; engines default to 256 MiB and 5 minutes (#1254).
- **File header** — page 0: an identity block written at creation and two alternating,
  separately checksummed header slots (LSN and sequence floors, checkpoint anchor, and a
  copy of the identity block), so a torn header write cannot make a file set unopenable.
  Page 0 is never a data page: the page manager refuses to pin or free it. Storage format 2;
  any other format is refused with `StorageFormatException` (`COHDBS001`), with no upgrade
  path (#1152).
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
