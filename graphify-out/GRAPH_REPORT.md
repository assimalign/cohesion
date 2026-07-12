# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4075 files · ~1,200,711 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 562 nodes · 1029 edges · 34 communities (17 shown, 17 thin omitted)
- Extraction: 90% EXTRACTED · 10% INFERRED · 0% AMBIGUOUS · INFERRED: 106 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `f3b01e1c`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- Storage
- StorageModelAlignmentTests
- SlottedPage
- StorageStream
- Assimalign.Cohesion.Database.Storage
- StorageBufferPool
- BufferEntry
- StoragePageManager
- Crc32
- Assimalign.Cohesion.Database.Storage — Design
- 4. The work items (with blockers)
- StorageFreeSpaceMap
- Assimalign.Cohesion.Database.Indexing — Design
- StorageFileHeader
- Assimalign.Cohesion.Database.Storage.Tests.csproj
- byte
- int
- IStoragePageHandle
- PageId
- JournalTests
- byte
- Dictionary
- IJournalLogger
- int
- IStorage
- IStoragePageHandle
- Name
- object
- PageId
- StorageStream
- Span
- SeekOrigin
- StorageId
- StreamJournalLogger

## God Nodes (most connected - your core abstractions)
1. `Storage` - 47 edges
2. `StorageStream` - 32 edges
3. `Journal` - 29 edges
4. `Assimalign.Cohesion.Database.Storage` - 27 edges
5. `StorageBufferPool` - 23 edges
6. `IStorageTransaction` - 19 edges
7. `StorageTransaction` - 19 edges
8. `IJournal` - 15 edges
9. `StorageBufferPoolTests` - 15 edges
10. `IStorage` - 14 edges

## Surprising Connections (you probably didn't know these)
- `SqlDatabaseInstance` --references--> `SqlStorage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDatabaseInstance.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Storage/src/SqlStorage.cs
- `SqlDatabaseSession` --references--> `SqlStorage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDatabaseSession.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Storage/src/SqlStorage.cs
- `SqlDatabaseSession` --references--> `SqlQueryExecutor`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDatabaseSession.cs → resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlQueryExecutor.cs
- `SqlDatabaseTransaction` --references--> `IStorageTransaction`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDatabaseTransaction.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Abstractions/IStorageTransaction.cs
- `Journal` --implements--> `IJournal`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Storage/src/Journal/Journal.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Abstractions/IJournal.cs

## Import Cycles
- None detected.

## Communities (34 total, 17 thin omitted)

### Community 0 - "Storage"
Cohesion: 0.06
Nodes (40): IQueryExecutor, IReadOnlyDictionary, SqlQueryExecutor, CancellationToken, QueryRequest, QueryResult, SqlQueryRequest, Task (+32 more)

### Community 1 - "StorageModelAlignmentTests"
Cohesion: 0.18
Nodes (8): StorageModelAlignmentTests, TestStorage, PageId, ReadOnlyMemory, SlotIndex, StorageModel, Stream, TestStorage

### Community 2 - "SlottedPage"
Cohesion: 0.11
Nodes (15): IStorageUnit, IStorageUnitIterator, PageSlot, StorageUnitIterator, int, IStorageFreeSpaceMap, IStoragePageHandle, IStoragePageManager (+7 more)

### Community 3 - "StorageStream"
Cohesion: 0.11
Nodes (17): Assimalign.Cohesion.Database.Storage.Tests.TestObjects, HarnessStorage, MemoryStream, CrashRecoveryTests, HarnessStorage, Fact, IStorageTransaction, PageId (+9 more)

### Community 4 - "Assimalign.Cohesion.Database.Storage"
Cohesion: 0.05
Nodes (24): Assimalign.Cohesion.Database.Sql.Internal, Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Storage.Units, Assimalign.Cohesion.Database.Sql.Storage, Assimalign.Cohesion.Database.Storage.Tests, GCHandle, LinkedListNode, Page (+16 more)

### Community 5 - "StorageBufferPool"
Cohesion: 0.06
Nodes (26): Action, BufferEntry, Fact, IStorageBufferPool, LinkedList, Memory, StorageBufferPool, Dictionary (+18 more)

### Community 6 - "BufferEntry"
Cohesion: 0.22
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.10
Nodes (13): HashSet, IStorageFreeSpaceMap, IStoragePageManager, Queue, StorageFreeSpaceMap, long, PageId, StoragePageManager (+5 more)

### Community 8 - "Crc32"
Cohesion: 0.16
Nodes (8): Assimalign.Cohesion.Database.Storage.Internal, Crc32, ReadOnlySpan, uint, PageChecksum, PageId, ReadOnlySpan, Span

### Community 9 - "Assimalign.Cohesion.Database.Storage — Design"
Cohesion: 0.10
Nodes (18): AOT posture, Assimalign.Cohesion.Database.Storage — Design, Checkpoints, Design intent, Error model, Non-goals, Recovery replay rules, The buffer pool (+10 more)

### Community 10 - "4. The work items (with blockers)"
Cohesion: 0.14
Nodes (13): 1. How to run this across multiple sessions (read first), 2. Stages (dependency gates), 3. Lanes (what can run in parallel) + per-lane guardrails, 4. The work items (with blockers), 5. Progress Log (orchestrator-reconciled from merged PRs), 6. Fast reference, Database Program Plan (Data Platform, L03.02), MVP definition (what "done" means for the first cut) (+5 more)

### Community 12 - "Assimalign.Cohesion.Database.Indexing — Design"
Cohesion: 0.22
Nodes (8): AOT posture, Assimalign.Cohesion.Database.Indexing — Design, Byte-comparable keys, Entry references are opaque `ulong`s, Intent, Non-goals, Relationship to `Database.Storage`, Transactional binding

### Community 13 - "StorageFileHeader"
Cohesion: 0.06
Nodes (21): Assimalign.Cohesion.Database.Sql.Tests, IAsyncDisposable, IDisposable, SqlDurabilityTests, Fact, IDatabaseSession, SqlQueryRequest, Task (+13 more)

### Community 15 - "byte"
Cohesion: 0.11
Nodes (14): Journal, bool, byte, IEnumerable, int, IReadOnlyList, long, object (+6 more)

### Community 16 - "int"
Cohesion: 0.15
Nodes (15): IDatabase, IDatabaseSession, IDatabaseTransaction, SqlDatabaseSession, CancellationToken, IDatabaseTransaction, QueryRequest, QueryResult (+7 more)

### Community 17 - "IStoragePageHandle"
Cohesion: 0.15
Nodes (6): StreamJournal, bool, IEnumerable, ReadOnlyMemory, ReadOnlySpan, Stream

### Community 18 - "PageId"
Cohesion: 0.20
Nodes (8): DatabaseName, IDatabaseEngine, ISqlDatabase, SqlDatabaseInstance, bool, CancellationToken, IDatabaseSession, ValueTask

## Knowledge Gaps
- **34 isolated node(s):** `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails`, `Stage 1 — Kernel`, `Stage 2 — Kernel build-out + languages (all language items are parallel-safe from day one)` (+29 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **17 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Storage` connect `Storage` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Database.Storage`, `StorageBufferPool`, `StorageFileHeader`, `IStoragePageHandle`?**
  _High betweenness centrality (0.277) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Storage` connect `Assimalign.Cohesion.Database.Storage` to `StorageBufferPool`, `StoragePageManager`, `StorageFileHeader`, `byte`, `IStoragePageHandle`?**
  _High betweenness centrality (0.220) - this node is a cross-community bridge._
- **Why does `StorageStream` connect `StorageBufferPool` to `Storage`, `StorageStream`, `Assimalign.Cohesion.Database.Storage`, `StoragePageManager`, `StorageFileHeader`?**
  _High betweenness centrality (0.160) - this node is a cross-community bridge._
- **What connects `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails` to the rest of the system?**
  _34 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Storage` be split into smaller, more focused modules?**
  _Cohesion score 0.05536568694463431 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10582010582010581 - nodes in this community are weakly interconnected._
- **Should `StorageStream` be split into smaller, more focused modules?**
  _Cohesion score 0.113107822410148 - nodes in this community are weakly interconnected._