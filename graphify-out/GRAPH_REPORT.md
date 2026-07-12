# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4120 files · ~1,221,321 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1623 nodes · 3028 edges · 74 communities (55 shown, 19 thin omitted)
- Extraction: 93% EXTRACTED · 7% INFERRED · 0% AMBIGUOUS · INFERRED: 205 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `3993fcc7`
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
- .ShutdownFlush
- .ExecuteAsync
- Exception
- QueryStatementResult
- Assimalign.Cohesion.Database.Execution.csproj
- Assimalign.Cohesion.Database.Execution.Tests.csproj
- FailingCommitLog
- JournalTransactionLog
- BTreeIndexRegistration
- .Create

## God Nodes (most connected - your core abstractions)
1. `Storage` - 49 edges
2. `Assimalign.Cohesion.Database.Storage` - 31 edges
3. `StorageStream` - 30 edges
4. `Journal` - 29 edges
5. `DatabaseKeyWriter` - 28 edges
6. `BTreeIndex` - 25 edges
7. `DatabaseKeyReader` - 24 edges
8. `BTreeNode` - 23 edges
9. `StorageBufferPool` - 22 edges
10. `IStorage` - 19 edges

## Surprising Connections (you probably didn't know these)
- `DefaultIndexManager` --implements--> `IIndexRegistry`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Indexing/src/Internal/DefaultIndexManager.cs → resources/Database/Assimalign.Cohesion.Database.Indexing/src/Abstractions/IIndexRegistry.cs
- `BTreeIndexManagerOptions` --references--> `BTreeIndexRegistration`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Indexing/src/BTreeIndexManagerOptions.cs → resources/Database/Assimalign.Cohesion.Database.Indexing/src/BTreeIndexRegistration.cs
- `DefaultIndexManager` --references--> `BTreeIndexManagerOptions`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Indexing/src/Internal/DefaultIndexManager.cs → resources/Database/Assimalign.Cohesion.Database.Indexing/src/BTreeIndexManagerOptions.cs
- `BTreeCursor` --references--> `IndexKey`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Indexing/src/Internal/BTreeCursor.cs → resources/Database/Assimalign.Cohesion.Database.Indexing/src/IndexKey.cs
- `Storage` --implements--> `IStorage`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Storage/src/Storage.cs → resources/Database/Assimalign.Cohesion.Database.Storage/src/Abstractions/IStorage.cs

## Import Cycles
- None detected.

## Communities (74 total, 19 thin omitted)

### Community 1 - "StorageModelAlignmentTests"
Cohesion: 0.01
Nodes (224): Assimalign.Cohesion.SourceGeneration, Assimalign.Cohesion.ProjectTemplates, Assimalign.Cohesion, Assimalign.Cohesion.App.ApiManager.Refs, Assimalign.Cohesion.App.ApiManager.Runtime, Assimalign.Cohesion.App.ConfigurationStore.Refs, Assimalign.Cohesion.App.ConfigurationStore.Runtime, Assimalign.Cohesion.App.Database.Refs (+216 more)

### Community 2 - "SlottedPage"
Cohesion: 0.11
Nodes (15): IStorageUnit, IStorageUnitIterator, PageSlot, StorageUnitIterator, int, IStorageFreeSpaceMap, IStoragePageHandle, IStoragePageManager (+7 more)

### Community 3 - "StorageStream"
Cohesion: 0.08
Nodes (24): Assimalign.Cohesion.Database.Storage.Tests.TestObjects, HarnessStorage, MemoryStream, CrashSimulationStream, bool, byte, HarnessStorage, StorageModel (+16 more)

### Community 4 - "Assimalign.Cohesion.Database.Storage"
Cohesion: 0.11
Nodes (8): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Transactions, Assimalign.Cohesion.Database.Transactions.Tests, LockManager, TransactionLog, ITransactionLog, TransactionManager, VersionStore

### Community 5 - "StorageBufferPool"
Cohesion: 0.26
Nodes (5): Func, IReadOnlyList, DatabaseKeyEncodingTests, DatabaseKeyWriter, Fact

### Community 6 - "BufferEntry"
Cohesion: 0.22
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.07
Nodes (20): HashSet, IStorageFreeSpaceMap, IStoragePageManager, Queue, StorageFreeSpaceMap, long, PageId, StoragePageManager (+12 more)

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
Cohesion: 0.20
Nodes (9): AOT posture, Assimalign.Cohesion.Database.Indexing — Design, Byte-comparable keys, Entry references are opaque `ulong`s, Intent, Non-goals, Relationship to `Database.Storage`, The B+Tree implementation (+1 more)

### Community 13 - "StorageFileHeader"
Cohesion: 0.08
Nodes (27): Assimalign.Cohesion.Database.Sql.Tests, Assimalign.Cohesion.Database.Execution.Tests, IQueryPipelineStage, IQueryTransactionScope, QueryExpression, QueryRequest, QueryStatement, DiagnosticStage (+19 more)

### Community 15 - "byte"
Cohesion: 0.05
Nodes (24): IJournal, IReadOnlyList, PageId, ReadOnlySpan, Journal, bool, byte, IEnumerable (+16 more)

### Community 16 - "int"
Cohesion: 0.06
Nodes (34): IDatabase, IDatabaseSession, IDatabaseTransaction, IDisposable, IQueryExecutor, IReadOnlyDictionary, SqlDatabaseSession, CancellationToken (+26 more)

### Community 17 - "IStoragePageHandle"
Cohesion: 0.19
Nodes (10): IJournal, ILockManager, ITransactionLog, ITransactionManager, TransactionRecovery, TransactionRecoveryPlan, IJournal, TransactionRecoveryTests (+2 more)

### Community 18 - "PageId"
Cohesion: 0.20
Nodes (8): DatabaseName, IDatabaseEngine, ISqlDatabase, SqlDatabaseInstance, bool, CancellationToken, IDatabaseSession, ValueTask

### Community 19 - "JournalTests"
Cohesion: 0.23
Nodes (3): Assimalign.Cohesion.Database.Storage.Tests, JournalTests, Fact

### Community 20 - "byte"
Cohesion: 0.35
Nodes (7): byte, InMemoryTransactionLog, CancellationToken, List, object, TransactionSequence, ValueTask

### Community 21 - "Dictionary"
Cohesion: 0.25
Nodes (8): Dictionary, IDictionary, QueryExecutionContext, CancellationToken, Diagnostic, IReadOnlyList, List, QueryRequest

### Community 31 - "SeekOrigin"
Cohesion: 0.19
Nodes (10): IIndexManager, IndexDefinition, DefaultIndexManager, CancellationToken, Dictionary, IIndex, IReadOnlyList, ITransactionContext (+2 more)

### Community 34 - "Assimalign.Cohesion.Resources.slnx"
Cohesion: 0.02
Nodes (93): Assimalign.Cohesion.Configuration, Assimalign.Cohesion.Connections.Quic, Assimalign.Cohesion.Connections.Security, Assimalign.Cohesion.Connections.Tcp, Assimalign.Cohesion.DependencyInjection, Assimalign.Cohesion.FileSystem, Assimalign.Cohesion.Http.Connections, Assimalign.Cohesion.Http.Cookies (+85 more)

### Community 35 - "Assimalign.Cohesion.Database.slnx"
Cohesion: 0.02
Nodes (91): Assimalign.Cohesion.ApplicationModel, Assimalign.Cohesion.Connections, Assimalign.Cohesion.Core, Assimalign.Cohesion.Hosting, Assimalign.Cohesion.Database.ApplicationModel, Assimalign.Cohesion.Database.ApplicationModel.Tests, Assimalign.Cohesion.Database.Blob.Catalog, Assimalign.Cohesion.Database.Blob.Catalog.Tests (+83 more)

### Community 36 - "DatabaseKeyReader"
Cohesion: 0.17
Nodes (10): DatabaseType, DatabaseKeyReader, DateOnly, DateTime, DateTimeOffset, Guid, int, ReadOnlySpan (+2 more)

### Community 37 - "DatabaseKeyWriter"
Cohesion: 0.14
Nodes (10): DatabaseKeyWriter, byte, DateOnly, DateTime, DateTimeOffset, Guid, int, ReadOnlySpan (+2 more)

### Community 38 - "DatabaseKeyEncodingTests"
Cohesion: 0.25
Nodes (9): Locks, Manager, TransactionManagerTests, Fact, ILockManager, ITransactionManager, IVersionStore, Task (+1 more)

### Community 39 - "StorageBufferPool"
Cohesion: 0.15
Nodes (7): DatabaseException, DatabaseKeyWriter, IComparable, IEquatable, IndexUniqueViolationException, IndexKey, ReadOnlyMemory

### Community 40 - "Fact"
Cohesion: 0.05
Nodes (29): Action, BufferEntry, GCHandle, IStorageBufferPool, LinkedList, LinkedListNode, Memory, Page (+21 more)

### Community 41 - "Assimalign.Cohesion.Database.Types — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Types — Design, Design intent, Error model, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Types — Overview, Dependencies (+2 more)

### Community 42 - "BufferEntry"
Cohesion: 0.10
Nodes (27): Fact, Harness, Index, IStorageTransactionSource, ITransactionManager, Reference, QueryPipelineTests, QueryResultStatus (+19 more)

### Community 43 - ".DisposeAsync"
Cohesion: 0.07
Nodes (30): IJournal, ValueTask, Storage, bool, Dictionary, IStorageBufferPool, IStorageFreeSpaceMap, IStoragePageHandle (+22 more)

### Community 44 - "StorageCorruptionException"
Cohesion: 0.25
Nodes (5): JournalException, StorageCorruptionException, PageId, StorageTransactionException, StorageException

### Community 45 - "StorageFileHeader"
Cohesion: 0.25
Nodes (6): StorageFileHeader, byte, int, long, MethodImpl, StorageModel

### Community 46 - "Assimalign.Cohesion.Database.Sql.Internal"
Cohesion: 0.15
Nodes (7): Assimalign.Cohesion.Database.Sql.Internal, Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Storage.Units, Assimalign.Cohesion.Database.Sql.Storage, StorageRecovery, PageFlags, PageType

### Community 47 - ".FlushAll"
Cohesion: 0.10
Nodes (24): ITransactionContext, IVersionStore, CancellationToken, ReadOnlyMemory, TransactionSequence, TransactionSnapshot, ValueTask, DefaultTransactionContext (+16 more)

### Community 48 - "Assimalign.Cohesion.Database.Types"
Cohesion: 0.18
Nodes (6): Assimalign.Cohesion.Database.Transactions, Assimalign.Cohesion.Database.Indexing.Tests, Assimalign.Cohesion.Database.Indexing, Assimalign.Cohesion.Database.Indexing.Tests.TestObjects, Assimalign.Cohesion.Database.Types, Microsoft.NET.Sdk

### Community 50 - "DefaultLockManager"
Cohesion: 0.15
Nodes (16): ILockManager, LockEntry, LockMode, LockResource, DefaultLockManager, LockEntry, Waiter, bool (+8 more)

### Community 51 - "StorageTransaction"
Cohesion: 0.16
Nodes (6): Digits, Exponent, KeyComponentEncoding, byte, int, ReadOnlySpan

### Community 52 - "StoragePageManager"
Cohesion: 0.37
Nodes (5): ILockManager, LockManagerTests, Fact, Task, TransactionSequence

### Community 53 - "StorageFreeSpaceMap"
Cohesion: 0.20
Nodes (3): CompareInfo, Assimalign.Cohesion.Database.Types, Collation

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
Cohesion: 0.22
Nodes (6): IIndexCursor, BTreeCursor, CancellationToken, int, List, ValueTask

### Community 58 - ".ExecuteAsync"
Cohesion: 0.47
Nodes (4): VersionStoreTests, Fact, Task, TransactionSnapshot

### Community 59 - "Assimalign.Cohesion.Database.Execution — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Execution — Design, Design intent, Lifecycle pattern, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Execution — Overview, Dependencies (+2 more)

### Community 60 - "IStorage"
Cohesion: 0.06
Nodes (43): EntryReference, IIndex, IndexKind, Promoted, ReaderWriterLockSlim, IStorageTransactionSource, IStorageTransaction, ITransactionContext (+35 more)

### Community 61 - ".ExecuteAsync"
Cohesion: 0.38
Nodes (5): BuiltQueryPipeline, CancellationToken, QueryPipelineDelegate, QueryResult, ValueTask

### Community 63 - "IQueryTransactionScope"
Cohesion: 0.28
Nodes (5): IAsyncDisposable, IQueryTransactionScope, CancellationToken, ValueTask, QueryTransactionStatus

### Community 64 - ".ShutdownFlush"
Cohesion: 0.32
Nodes (3): Assimalign.Cohesion.Database.Types.Tests, CollationTests, Fact

### Community 65 - ".ExecuteAsync"
Cohesion: 0.33
Nodes (4): IQueryPipeline, CancellationToken, QueryResult, ValueTask

### Community 66 - "Exception"
Cohesion: 0.40
Nodes (3): Exception, QueryExecutionException, DatabaseTypeException

### Community 67 - "QueryStatementResult"
Cohesion: 0.40
Nodes (5): QueryResult, QueryStatementResult, Diagnostic, IReadOnlyList, QueryResultStatus

### Community 70 - "FailingCommitLog"
Cohesion: 0.46
Nodes (5): ITransactionLog, FailingCommitLog, CancellationToken, TransactionSequence, ValueTask

### Community 71 - "JournalTransactionLog"
Cohesion: 0.46
Nodes (5): JournalTransactionLog, CancellationToken, IJournal, TransactionSequence, ValueTask

### Community 72 - "BTreeIndexRegistration"
Cohesion: 0.33
Nodes (3): IIndexRegistry, IReadOnlyList, BTreeIndexRegistration

## Knowledge Gaps
- **296 isolated node(s):** `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails`, `Stage 1 — Kernel`, `Stage 2 — Kernel build-out + languages (all language items are parallel-safe from day one)` (+291 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **19 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Assimalign.Cohesion.Database.Storage` connect `Assimalign.Cohesion.Database.Sql.Internal` to `StoragePageManager`, `Fact`, `.DisposeAsync`, `StorageCorruptionException`, `StorageFileHeader`, `byte`, `Assimalign.Cohesion.Database.Types`, `int`, `JournalTests`, `IStorage`?**
  _High betweenness centrality (0.329) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Transactions` connect `Assimalign.Cohesion.Database.Types` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `Assimalign.Cohesion.Database.slnx`?**
  _High betweenness centrality (0.262) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Storage` connect `Assimalign.Cohesion.Database.Storage` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `Assimalign.Cohesion.Database.slnx`, `IStoragePageHandle`?**
  _High betweenness centrality (0.125) - this node is a cross-community bridge._
- **What connects `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails` to the rest of the system?**
  _296 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `StorageModelAlignmentTests` be split into smaller, more focused modules?**
  _Cohesion score 0.008888888888888889 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10582010582010581 - nodes in this community are weakly interconnected._
- **Should `StorageStream` be split into smaller, more focused modules?**
  _Cohesion score 0.07562008469449485 - nodes in this community are weakly interconnected._