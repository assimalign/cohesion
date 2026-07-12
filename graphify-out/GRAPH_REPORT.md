# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4107 files · ~1,214,637 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1423 nodes · 2589 edges · 70 communities (50 shown, 20 thin omitted)
- Extraction: 95% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 136 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `679e344e`
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
- .ExecuteAsync
- Assimalign.Cohesion.Database.Execution — Design
- IStorage
- .ExecuteAsync
- Assimalign.Cohesion.Database.Execution
- IQueryTransactionScope
- .ExecuteAsync
- Exception
- QueryStatementResult
- Assimalign.Cohesion.Database.Execution.csproj
- Assimalign.Cohesion.Database.Execution.Tests.csproj

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
- `IQueryTransactionScope` --references--> `QueryTransactionStatus`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Execution/src/Abstractions/IQueryTransactionScope.cs → resources/Database/Assimalign.Cohesion.Database.Execution/src/QueryTransactionStatus.cs
- `BuiltQueryPipeline` --implements--> `IQueryPipeline`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Execution/src/Internal/BuiltQueryPipeline.cs → resources/Database/Assimalign.Cohesion.Database.Execution/src/Abstractions/IQueryPipeline.cs
- `QueryExecutionContext` --references--> `IQueryTransactionScope`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Execution/src/QueryExecutionContext.cs → resources/Database/Assimalign.Cohesion.Database.Execution/src/Abstractions/IQueryTransactionScope.cs
- `InMemoryVersionStore` --implements--> `IVersionStore`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Transactions/src/Internal/InMemoryVersionStore.cs → resources/Database/Assimalign.Cohesion.Database.Transactions/src/Abstractions/IVersionStore.cs
- `SqlDatabaseInstance` --references--> `SqlStorage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/SqlDatabaseInstance.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Storage/src/SqlStorage.cs

## Import Cycles
- None detected.

## Communities (70 total, 20 thin omitted)

### Community 0 - "Storage"
Cohesion: 0.24
Nodes (6): PageId, ReadOnlySpan, SlotIndex, IStorageTransaction, ReadOnlySpan, SlotIndex

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
Cohesion: 0.16
Nodes (6): PageId, StorageStream, PageId, ReadOnlySpan, SeekOrigin, Span

### Community 6 - "BufferEntry"
Cohesion: 0.22
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.10
Nodes (14): HashSet, IStorageFreeSpaceMap, Queue, StorageFreeSpaceMap, long, PageId, StorageModelAlignmentTests, TestStorage (+6 more)

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
Cohesion: 0.08
Nodes (27): Assimalign.Cohesion.Database.Sql.Tests, Assimalign.Cohesion.Database.Execution.Tests, IQueryPipelineStage, IQueryTransactionScope, QueryExpression, QueryRequest, QueryStatement, DiagnosticStage (+19 more)

### Community 15 - "byte"
Cohesion: 0.07
Nodes (19): IDisposable, IJournal, IReadOnlyList, PageId, ReadOnlySpan, Journal, bool, byte (+11 more)

### Community 16 - "int"
Cohesion: 0.17
Nodes (13): IDatabase, IDatabaseSession, IDatabaseTransaction, SqlDatabaseSession, CancellationToken, IDatabaseTransaction, QueryRequest, QueryResult (+5 more)

### Community 17 - "IStoragePageHandle"
Cohesion: 0.15
Nodes (6): StreamJournal, bool, IEnumerable, ReadOnlyMemory, ReadOnlySpan, Stream

### Community 18 - "PageId"
Cohesion: 0.20
Nodes (8): DatabaseName, IDatabaseEngine, ISqlDatabase, SqlDatabaseInstance, bool, CancellationToken, IDatabaseSession, ValueTask

### Community 20 - "byte"
Cohesion: 0.12
Nodes (17): byte, ITransactionLog, InMemoryTransactionLog, CancellationToken, List, object, TransactionSequence, ValueTask (+9 more)

### Community 21 - "Dictionary"
Cohesion: 0.25
Nodes (8): Dictionary, IDictionary, QueryExecutionContext, CancellationToken, Diagnostic, IReadOnlyList, List, QueryRequest

### Community 34 - "Assimalign.Cohesion.Resources.slnx"
Cohesion: 0.02
Nodes (94): Assimalign.Cohesion.ApplicationModel, Assimalign.Cohesion.Configuration, Assimalign.Cohesion.Connections.Quic, Assimalign.Cohesion.Connections.Security, Assimalign.Cohesion.Connections.Tcp, Assimalign.Cohesion.DependencyInjection, Assimalign.Cohesion.FileSystem, Assimalign.Cohesion.Http.Connections (+86 more)

### Community 35 - "Assimalign.Cohesion.Database.slnx"
Cohesion: 0.02
Nodes (92): Assimalign.Cohesion.Connections, Assimalign.Cohesion.Core, Assimalign.Cohesion.Hosting, Assimalign.Cohesion.Database.ApplicationModel, Assimalign.Cohesion.Database.ApplicationModel.Tests, Assimalign.Cohesion.Database.Blob.Catalog, Assimalign.Cohesion.Database.Blob.Catalog.Tests, Assimalign.Cohesion.Database.Blob.Client (+84 more)

### Community 36 - "DatabaseKeyReader"
Cohesion: 0.08
Nodes (19): Action, Assimalign.Cohesion.Database.Types.Tests, DatabaseType, Func, IReadOnlyList, DatabaseKeyReader, DateOnly, DateTime (+11 more)

### Community 37 - "DatabaseKeyWriter"
Cohesion: 0.07
Nodes (19): CompareInfo, Assimalign.Cohesion.Database.Types, Digits, Exponent, Collation, DatabaseKeyWriter, byte, DateOnly (+11 more)

### Community 38 - "DatabaseKeyEncodingTests"
Cohesion: 0.05
Nodes (36): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Transactions, Assimalign.Cohesion.Database.Transactions.Tests, Locks, Manager, LockManager, ILockManager, TransactionLog (+28 more)

### Community 39 - "StorageBufferPool"
Cohesion: 0.13
Nodes (14): BufferEntry, GCHandle, IStorageBufferPool, LinkedList, LinkedListNode, Page, BufferEntry, StorageBufferPool (+6 more)

### Community 40 - "Fact"
Cohesion: 0.21
Nodes (3): IStoragePageHandle, StorageBufferPoolTests, StorageStream

### Community 41 - "Assimalign.Cohesion.Database.Types — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Types — Design, Design intent, Error model, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Types — Overview, Dependencies (+2 more)

### Community 42 - "BufferEntry"
Cohesion: 0.42
Nodes (4): Fact, QueryPipelineTests, QueryResultStatus, Task

### Community 43 - ".DisposeAsync"
Cohesion: 0.11
Nodes (17): Storage, bool, Dictionary, IStorageBufferPool, IStorageFreeSpaceMap, IStoragePageManager, IStorageUnitIterator, long (+9 more)

### Community 44 - "StorageCorruptionException"
Cohesion: 0.25
Nodes (5): JournalException, StorageCorruptionException, PageId, StorageTransactionException, StorageException

### Community 45 - "StorageFileHeader"
Cohesion: 0.25
Nodes (6): StorageFileHeader, byte, int, long, MethodImpl, StorageModel

### Community 46 - "Assimalign.Cohesion.Database.Sql.Internal"
Cohesion: 0.14
Nodes (6): Assimalign.Cohesion.Database.Sql.Internal, Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Sql.Storage, StorageRecovery, PageFlags, PageType

### Community 47 - ".FlushAll"
Cohesion: 0.09
Nodes (27): ITransactionContext, ITransactionManager, IVersionStore, CancellationToken, ReadOnlyMemory, TransactionSequence, TransactionSnapshot, ValueTask (+19 more)

### Community 50 - "DefaultLockManager"
Cohesion: 0.15
Nodes (16): ILockManager, LockEntry, LockMode, LockResource, DefaultLockManager, LockEntry, Waiter, bool (+8 more)

### Community 51 - "StorageTransaction"
Cohesion: 0.20
Nodes (6): IReadOnlyDictionary, StorageTransaction, bool, Dictionary, IStoragePageHandle, SlottedPage

### Community 52 - "StoragePageManager"
Cohesion: 0.20
Nodes (6): IStoragePageManager, StoragePageManager, CancellationToken, IStoragePageHandle, PageId, ValueTask

### Community 53 - "StorageFreeSpaceMap"
Cohesion: 0.24
Nodes (6): Memory, ValueTask, CancellationToken, ReadOnlyMemory, Task, ValueTask

### Community 54 - "Assimalign.Cohesion.Database.Transactions — Design"
Cohesion: 0.17
Nodes (11): AOT posture, Assimalign.Cohesion.Database.Transactions — Design, Error model, Identity vs. ordering: `TransactionId` vs. `TransactionSequence`, Intent, Non-goals, Snapshot semantics, The lock manager implementation (+3 more)

### Community 55 - "InMemoryVersionStore"
Cohesion: 0.24
Nodes (9): InMemoryVersionStore, Version, CancellationToken, Dictionary, object, ReadOnlyMemory, TransactionSequence, TransactionSnapshot (+1 more)

### Community 56 - ".InsertRecord"
Cohesion: 0.17
Nodes (8): IQueryPipelineStage, CancellationToken, QueryPipelineDelegate, QueryResult, ValueTask, QueryPipelineBuilder, List, QueryPipelineDelegate

### Community 57 - "SqlStorage"
Cohesion: 0.29
Nodes (4): SqlStorage, ReadOnlyMemory, StorageModel, Stream

### Community 58 - ".ExecuteAsync"
Cohesion: 0.33
Nodes (7): IQueryExecutor, SqlQueryExecutor, CancellationToken, QueryRequest, QueryResult, SqlQueryRequest, Task

### Community 59 - "Assimalign.Cohesion.Database.Execution — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Execution — Design, Design intent, Lifecycle pattern, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Execution — Overview, Dependencies (+2 more)

### Community 60 - "IStorage"
Cohesion: 0.17
Nodes (8): IStorage, IStorageBufferPool, IStorageFreeSpaceMap, IStoragePageManager, IStorageUnitIterator, Name, StorageId, StorageModel

### Community 61 - ".ExecuteAsync"
Cohesion: 0.32
Nodes (5): BuiltQueryPipeline, CancellationToken, QueryPipelineDelegate, QueryResult, ValueTask

### Community 62 - "Assimalign.Cohesion.Database.Execution"
Cohesion: 0.33
Nodes (3): Assimalign.Cohesion.Database.Language, Assimalign.Cohesion.Database.Execution, QueryTransactionStatus

### Community 63 - "IQueryTransactionScope"
Cohesion: 0.38
Nodes (4): IAsyncDisposable, IQueryTransactionScope, CancellationToken, ValueTask

### Community 65 - ".ExecuteAsync"
Cohesion: 0.33
Nodes (4): IQueryPipeline, CancellationToken, QueryResult, ValueTask

### Community 66 - "Exception"
Cohesion: 0.40
Nodes (3): Exception, QueryExecutionException, DatabaseTypeException

### Community 67 - "QueryStatementResult"
Cohesion: 0.40
Nodes (5): QueryResult, QueryStatementResult, Diagnostic, IReadOnlyList, QueryResultStatus

## Knowledge Gaps
- **294 isolated node(s):** `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails`, `Stage 1 — Kernel`, `Stage 2 — Kernel build-out + languages (all language items are parallel-safe from day one)` (+289 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **20 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Assimalign.Cohesion.Database.Language` connect `Assimalign.Cohesion.Database.Execution` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `Assimalign.Cohesion.Database.slnx`, `StorageFileHeader`?**
  _High betweenness centrality (0.325) - this node is a cross-community bridge._
- **Why does `Storage` connect `.DisposeAsync` to `Storage`, `.ShutdownFlush`, `StorageBufferPool`, `StorageBufferPool`, `StoragePageManager`, `Assimalign.Cohesion.Database.Sql.Internal`, `byte`, `IStoragePageHandle`, `StorageTransaction`, `StorageFreeSpaceMap`, `SqlStorage`, `IStorage`?**
  _High betweenness centrality (0.141) - this node is a cross-community bridge._
- **Why does `QueryExecutionContext` connect `Dictionary` to `.ExecuteAsync`, `.InsertRecord`, `.ExecuteAsync`, `Assimalign.Cohesion.Database.Execution`, `IQueryTransactionScope`?**
  _High betweenness centrality (0.137) - this node is a cross-community bridge._
- **What connects `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails` to the rest of the system?**
  _294 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `StorageModelAlignmentTests` be split into smaller, more focused modules?**
  _Cohesion score 0.008888888888888889 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10582010582010581 - nodes in this community are weakly interconnected._
- **Should `StorageStream` be split into smaller, more focused modules?**
  _Cohesion score 0.113107822410148 - nodes in this community are weakly interconnected._