# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4079 files · ~1,206,096 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1116 nodes · 2018 edges · 50 communities (29 shown, 21 thin omitted)
- Extraction: 93% EXTRACTED · 7% INFERRED · 0% AMBIGUOUS · INFERRED: 132 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `739f0a53`
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
- Assimalign.Cohesion.Database.Types
- Assimalign.Cohesion.Database.Types.Tests

## God Nodes (most connected - your core abstractions)
1. `Storage` - 47 edges
2. `StorageStream` - 32 edges
3. `Journal` - 29 edges
4. `DatabaseKeyWriter` - 28 edges
5. `Assimalign.Cohesion.Database.Storage` - 27 edges
6. `DatabaseKeyReader` - 24 edges
7. `StorageBufferPool` - 23 edges
8. `IStorageTransaction` - 19 edges
9. `StorageTransaction` - 19 edges
10. `DatabaseKeyEncodingTests` - 16 edges

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

## Communities (50 total, 21 thin omitted)

### Community 0 - "Storage"
Cohesion: 0.06
Nodes (39): IQueryExecutor, IReadOnlyDictionary, SqlQueryExecutor, CancellationToken, QueryRequest, QueryResult, SqlQueryRequest, Task (+31 more)

### Community 1 - "StorageModelAlignmentTests"
Cohesion: 0.01
Nodes (225): Assimalign.Cohesion.SourceGeneration, Assimalign.Cohesion.Build.Tasks, Assimalign.Cohesion.ProjectTemplates, Assimalign.Cohesion, Assimalign.Cohesion.App.ApiManager.Refs, Assimalign.Cohesion.App.ApiManager.Runtime, Assimalign.Cohesion.App.ConfigurationStore.Refs, Assimalign.Cohesion.App.ConfigurationStore.Runtime (+217 more)

### Community 2 - "SlottedPage"
Cohesion: 0.11
Nodes (15): IStorageUnit, IStorageUnitIterator, PageSlot, StorageUnitIterator, int, IStorageFreeSpaceMap, IStoragePageHandle, IStoragePageManager (+7 more)

### Community 3 - "StorageStream"
Cohesion: 0.11
Nodes (17): Assimalign.Cohesion.Database.Storage.Tests.TestObjects, HarnessStorage, MemoryStream, CrashRecoveryTests, HarnessStorage, Fact, IStorageTransaction, PageId (+9 more)

### Community 4 - "Assimalign.Cohesion.Database.Storage"
Cohesion: 0.12
Nodes (5): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Storage.Units, Assimalign.Cohesion.Database.Storage.Tests, StorageRecovery, PageFlags

### Community 5 - "StorageBufferPool"
Cohesion: 0.12
Nodes (11): Memory, Stream, StorageStream, CancellationToken, PageId, ReadOnlyMemory, ReadOnlySpan, SeekOrigin (+3 more)

### Community 6 - "BufferEntry"
Cohesion: 0.23
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.07
Nodes (21): HashSet, IStorageFreeSpaceMap, IStoragePageManager, Queue, StorageFreeSpaceMap, long, PageId, StoragePageManager (+13 more)

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

### Community 34 - "Assimalign.Cohesion.Resources.slnx"
Cohesion: 0.02
Nodes (96): Assimalign.Cohesion.Configuration, Assimalign.Cohesion.Connections.Quic, Assimalign.Cohesion.Connections.Security, Assimalign.Cohesion.Connections.Tcp, Assimalign.Cohesion.DependencyInjection, Assimalign.Cohesion.FileSystem, Assimalign.Cohesion.Http.Connections, Assimalign.Cohesion.Http.Cookies (+88 more)

### Community 35 - "Assimalign.Cohesion.Database.slnx"
Cohesion: 0.02
Nodes (94): Assimalign.Cohesion.ApplicationModel, Assimalign.Cohesion.Connections, Assimalign.Cohesion.Core, Assimalign.Cohesion.Hosting, Assimalign.Cohesion.Database.ApplicationModel, Assimalign.Cohesion.Database.Blob.Catalog, Assimalign.Cohesion.Database.Blob.Catalog.Tests, Assimalign.Cohesion.Database.Blob.Client (+86 more)

### Community 36 - "DatabaseKeyReader"
Cohesion: 0.09
Nodes (15): CompareInfo, Assimalign.Cohesion.Database.Types, DatabaseType, Exception, Collation, DatabaseKeyReader, DateOnly, DateTime (+7 more)

### Community 37 - "DatabaseKeyWriter"
Cohesion: 0.09
Nodes (16): Digits, Exponent, DatabaseKeyWriter, byte, DateOnly, DateTime, DateTimeOffset, Guid (+8 more)

### Community 38 - "DatabaseKeyEncodingTests"
Cohesion: 0.16
Nodes (8): Assimalign.Cohesion.Database.Types.Tests, Func, IReadOnlyList, CollationTests, Fact, DatabaseKeyEncodingTests, DatabaseKeyWriter, Fact

### Community 39 - "StorageBufferPool"
Cohesion: 0.19
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

### Community 44 - "StorageCorruptionException"
Cohesion: 0.25
Nodes (5): JournalException, StorageCorruptionException, PageId, StorageTransactionException, StorageException

### Community 45 - "StorageFileHeader"
Cohesion: 0.25
Nodes (6): StorageFileHeader, byte, int, long, MethodImpl, StorageModel

## Knowledge Gaps
- **273 isolated node(s):** `Assimalign.Cohesion.SourceGeneration`, `Assimalign.Cohesion.Build.Tasks`, `Assimalign.Cohesion.ProjectTemplates`, `Assimalign.Cohesion`, `Assimalign.Cohesion.App.ApiManager.Refs` (+268 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **21 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `StorageBufferPool` connect `StorageBufferPool` to `Storage`, `StoragePageManager`, `Fact`, `BufferEntry`, `.FlushAll`?**
  _High betweenness centrality (0.126) - this node is a cross-community bridge._
- **Why does `Storage` connect `Storage` to `Assimalign.Cohesion.Database.Storage`, `StorageBufferPool`, `StorageBufferPool`, `StoragePageManager`, `.DisposeAsync`, `StorageFileHeader`, `IStoragePageHandle`?**
  _High betweenness centrality (0.117) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Storage` connect `Assimalign.Cohesion.Database.Storage` to `StoragePageManager`, `BufferEntry`, `StorageCorruptionException`, `StorageFileHeader`, `StorageFileHeader`, `Assimalign.Cohesion.Database.Sql.Internal`, `byte`, `IStoragePageHandle`?**
  _High betweenness centrality (0.065) - this node is a cross-community bridge._
- **What connects `Assimalign.Cohesion.SourceGeneration`, `Assimalign.Cohesion.Build.Tasks`, `Assimalign.Cohesion.ProjectTemplates` to the rest of the system?**
  _273 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Storage` be split into smaller, more focused modules?**
  _Cohesion score 0.058496853017400964 - nodes in this community are weakly interconnected._
- **Should `StorageModelAlignmentTests` be split into smaller, more focused modules?**
  _Cohesion score 0.008849557522123894 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10582010582010581 - nodes in this community are weakly interconnected._