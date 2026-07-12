# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4094 files · ~1,211,279 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1314 nodes · 2407 edges · 58 communities (39 shown, 19 thin omitted)
- Extraction: 94% EXTRACTED · 6% INFERRED · 0% AMBIGUOUS · INFERRED: 134 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `9211d51c`
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
- Assimalign.Cohesion.Resources.slnx
- Assimalign.Cohesion.Database.slnx
- DatabaseKeyReader
- DatabaseKeyWriter
- DatabaseKeyEncodingTests
- StorageBufferPool
- Fact
- Assimalign.Cohesion.Database.Types — Design
- BufferEntry
- .DisposeAsync
- StorageCorruptionException
- StorageFileHeader
- Assimalign.Cohesion.Database.Sql.Internal
- .FlushAll
- Assimalign.Cohesion.Database.Types
- Assimalign.Cohesion.Database.Types.Tests
- DefaultLockManager
- StorageTransaction
- StoragePageManager
- StorageFreeSpaceMap
- Assimalign.Cohesion.Database.Transactions — Design
- InMemoryVersionStore
- .InsertRecord
- SqlStorage

## God Nodes (most connected - your core abstractions)
1. `Storage` - 47 edges
2. `StorageStream` - 32 edges
3. `Journal` - 29 edges
4. `DatabaseKeyWriter` - 28 edges
5. `Assimalign.Cohesion.Database.Storage` - 27 edges
6. `DatabaseKeyReader` - 24 edges
7. `StorageBufferPool` - 23 edges
8. `DefaultTransactionManager` - 19 edges
9. `IStorageTransaction` - 19 edges
10. `StorageTransaction` - 19 edges

## Surprising Connections (you probably didn't know these)
- `InMemoryVersionStore` --implements--> `IVersionStore`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Transactions/src/Internal/InMemoryVersionStore.cs → resources/Database/Assimalign.Cohesion.Database.Transactions/src/Abstractions/IVersionStore.cs
- `SqlDatabaseInstance` --references--> `SqlStorage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDatabaseInstance.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Storage/src/SqlStorage.cs
- `SqlDatabaseSession` --references--> `SqlStorage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDatabaseSession.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Storage/src/SqlStorage.cs
- `SqlQueryExecutor` --references--> `SqlStorage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlQueryExecutor.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Storage/src/SqlStorage.cs
- `SqlStorage` --inherits--> `Storage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql.Storage/src/SqlStorage.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Storage.cs

## Import Cycles
- None detected.

## Communities (58 total, 19 thin omitted)

### Community 0 - "Storage"
Cohesion: 0.18
Nodes (11): IQueryExecutor, SqlQueryExecutor, CancellationToken, QueryRequest, QueryResult, SqlQueryRequest, Task, PageId (+3 more)

### Community 1 - "StorageModelAlignmentTests"
Cohesion: 0.01
Nodes (224): Assimalign.Cohesion.SourceGeneration, Assimalign.Cohesion.ProjectTemplates, Assimalign.Cohesion, Assimalign.Cohesion.App.ApiManager.Refs, Assimalign.Cohesion.App.ApiManager.Runtime, Assimalign.Cohesion.App.ConfigurationStore.Refs, Assimalign.Cohesion.App.ConfigurationStore.Runtime, Assimalign.Cohesion.App.Database.Refs (+216 more)

### Community 2 - "SlottedPage"
Cohesion: 0.11
Nodes (15): IStorageUnit, IStorageUnitIterator, PageSlot, StorageUnitIterator, int, IStorageFreeSpaceMap, IStoragePageHandle, IStoragePageManager (+7 more)

### Community 3 - "StorageStream"
Cohesion: 0.11
Nodes (17): Assimalign.Cohesion.Database.Storage.Tests.TestObjects, HarnessStorage, MemoryStream, CrashRecoveryTests, HarnessStorage, Fact, IStorageTransaction, PageId (+9 more)

### Community 5 - "StorageBufferPool"
Cohesion: 0.11
Nodes (12): Memory, IReadOnlyList, StorageRecovery, StorageStream, CancellationToken, PageId, ReadOnlyMemory, ReadOnlySpan (+4 more)

### Community 6 - "BufferEntry"
Cohesion: 0.22
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.18
Nodes (8): StorageModelAlignmentTests, TestStorage, PageId, ReadOnlyMemory, SlotIndex, StorageModel, Stream, TestStorage

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
Cohesion: 0.07
Nodes (20): Assimalign.Cohesion.Database.Sql.Tests, IAsyncDisposable, IDisposable, SqlDurabilityTests, Fact, IDatabaseSession, SqlQueryRequest, Task (+12 more)

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

### Community 20 - "byte"
Cohesion: 0.12
Nodes (17): byte, ITransactionLog, InMemoryTransactionLog, CancellationToken, List, object, TransactionSequence, ValueTask (+9 more)

### Community 34 - "Assimalign.Cohesion.Resources.slnx"
Cohesion: 0.02
Nodes (95): Assimalign.Cohesion.Configuration, Assimalign.Cohesion.Connections.Quic, Assimalign.Cohesion.Connections.Security, Assimalign.Cohesion.Connections.Tcp, Assimalign.Cohesion.DependencyInjection, Assimalign.Cohesion.FileSystem, Assimalign.Cohesion.Http.Connections, Assimalign.Cohesion.Http.Cookies (+87 more)

### Community 35 - "Assimalign.Cohesion.Database.slnx"
Cohesion: 0.02
Nodes (94): Assimalign.Cohesion.ApplicationModel, Assimalign.Cohesion.Connections, Assimalign.Cohesion.Core, Assimalign.Cohesion.Hosting, Assimalign.Cohesion.Database.ApplicationModel, Assimalign.Cohesion.Database.ApplicationModel.Tests, Assimalign.Cohesion.Database.Blob.Catalog.Tests, Assimalign.Cohesion.Database.Blob.Client (+86 more)

### Community 36 - "DatabaseKeyReader"
Cohesion: 0.07
Nodes (20): CompareInfo, Assimalign.Cohesion.Database.Types.Tests, DatabaseType, Func, IReadOnlyList, Collation, DatabaseKeyReader, DateOnly (+12 more)

### Community 37 - "DatabaseKeyWriter"
Cohesion: 0.07
Nodes (19): Assimalign.Cohesion.Database.Types, Digits, Exception, Exponent, DatabaseKeyWriter, byte, DateOnly, DateTime (+11 more)

### Community 38 - "DatabaseKeyEncodingTests"
Cohesion: 0.05
Nodes (37): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Transactions, Assimalign.Cohesion.Database.Transactions.Tests, Locks, Manager, Version, LockManager, ILockManager (+29 more)

### Community 39 - "StorageBufferPool"
Cohesion: 0.21
Nodes (9): Action, BufferEntry, IStorageBufferPool, LinkedList, StorageBufferPool, Dictionary, object, PageId (+1 more)

### Community 40 - "Fact"
Cohesion: 0.25
Nodes (5): Fact, IStoragePageHandle, StorageBufferPoolTests, StorageStream, Task

### Community 41 - "Assimalign.Cohesion.Database.Types — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Types — Design, Design intent, Error model, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Types — Overview, Dependencies (+2 more)

### Community 42 - "BufferEntry"
Cohesion: 0.22
Nodes (7): GCHandle, LinkedListNode, Page, BufferEntry, bool, byte, int

### Community 43 - ".DisposeAsync"
Cohesion: 0.10
Nodes (15): Storage, bool, Dictionary, IStorageBufferPool, IStorageFreeSpaceMap, IStoragePageManager, IStorageUnitIterator, long (+7 more)

### Community 44 - "StorageCorruptionException"
Cohesion: 0.25
Nodes (5): JournalException, StorageCorruptionException, PageId, StorageTransactionException, StorageException

### Community 45 - "StorageFileHeader"
Cohesion: 0.25
Nodes (6): StorageFileHeader, byte, int, long, MethodImpl, StorageModel

### Community 46 - "Assimalign.Cohesion.Database.Sql.Internal"
Cohesion: 0.20
Nodes (4): Assimalign.Cohesion.Database.Sql.Internal, Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Sql.Storage, PageFlags

### Community 47 - ".FlushAll"
Cohesion: 0.10
Nodes (25): ITransactionContext, ITransactionManager, IVersionStore, CancellationToken, ReadOnlyMemory, TransactionSequence, TransactionSnapshot, ValueTask (+17 more)

### Community 50 - "DefaultLockManager"
Cohesion: 0.15
Nodes (16): ILockManager, LockEntry, LockMode, LockResource, DefaultLockManager, LockEntry, Waiter, bool (+8 more)

### Community 51 - "StorageTransaction"
Cohesion: 0.18
Nodes (6): IReadOnlyDictionary, StorageTransaction, bool, Dictionary, IStoragePageHandle, SlottedPage

### Community 52 - "StoragePageManager"
Cohesion: 0.18
Nodes (7): IStoragePageManager, StoragePageManager, CancellationToken, IStoragePageHandle, PageId, ValueTask, PageType

### Community 53 - "StorageFreeSpaceMap"
Cohesion: 0.20
Nodes (6): HashSet, IStorageFreeSpaceMap, Queue, StorageFreeSpaceMap, long, PageId

### Community 54 - "Assimalign.Cohesion.Database.Transactions — Design"
Cohesion: 0.17
Nodes (11): AOT posture, Assimalign.Cohesion.Database.Transactions — Design, Error model, Identity vs. ordering: `TransactionId` vs. `TransactionSequence`, Intent, Non-goals, Snapshot semantics, The lock manager implementation (+3 more)

### Community 55 - "InMemoryVersionStore"
Cohesion: 0.30
Nodes (8): InMemoryVersionStore, CancellationToken, Dictionary, object, ReadOnlyMemory, TransactionSequence, TransactionSnapshot, ValueTask

### Community 56 - ".InsertRecord"
Cohesion: 0.31
Nodes (5): PageId, ReadOnlyMemory, ReadOnlySpan, SlotIndex, StorageTuple

### Community 57 - "SqlStorage"
Cohesion: 0.29
Nodes (4): SqlStorage, ReadOnlyMemory, StorageModel, Stream

## Knowledge Gaps
- **283 isolated node(s):** `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails`, `Stage 1 — Kernel`, `Stage 2 — Kernel build-out + languages (all language items are parallel-safe from day one)` (+278 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **19 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `SqlDatabaseTransaction` connect `int` to `Storage`, `Assimalign.Cohesion.Database.Sql.Internal`?**
  _High betweenness centrality (0.435) - this node is a cross-community bridge._
- **Why does `DefaultTransactionContext` connect `.FlushAll` to `int`?**
  _High betweenness centrality (0.434) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Storage` connect `DatabaseKeyEncodingTests` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `Assimalign.Cohesion.Database.slnx`?**
  _High betweenness centrality (0.405) - this node is a cross-community bridge._
- **What connects `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails` to the rest of the system?**
  _283 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `StorageModelAlignmentTests` be split into smaller, more focused modules?**
  _Cohesion score 0.008888888888888889 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10582010582010581 - nodes in this community are weakly interconnected._
- **Should `StorageStream` be split into smaller, more focused modules?**
  _Cohesion score 0.113107822410148 - nodes in this community are weakly interconnected._