# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4125 files · ~1,224,384 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1981 nodes · 3945 edges · 98 communities (75 shown, 23 thin omitted)
- Extraction: 91% EXTRACTED · 9% INFERRED · 0% AMBIGUOUS · INFERRED: 339 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `99b64791`
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
- SqlSelectParserTests
- StorageBufferPool
- .IsKeyword
- .Pin
- .ExecuteAsync
- BufferEntry
- SqlDatabaseSession
- SqlDdlParserTests
- SqlDurabilityTests
- Assimalign.Cohesion.Database.Sql.Language.Tests
- SqlQueryParserTests
- SqlQueryStatement
- SqlDatabaseTransaction
- .ParseInsert
- SqlInsertParserTests
- SqlDeleteParserTests
- Assimalign.Cohesion.Database.Sql.Internal
- SqlUpdateParserTests
- BTreeIndexTests.cs
- Assimalign.Cohesion.Database.Storage
- SqlCaseExpression
- CancellationToken
- QueryResult
- IDatabaseSession

## God Nodes (most connected - your core abstractions)
1. `Assimalign.Cohesion.Database.Sql.Language` - 53 edges
2. `Storage` - 49 edges
3. `SqlExpression` - 44 edges
4. `Assimalign.Cohesion.Database.Language` - 42 edges
5. `SqlExecutionPipelineTests` - 33 edges
6. `SqlExpressionParserTests` - 32 edges
7. `Assimalign.Cohesion.Database.Storage` - 30 edges
8. `StorageStream` - 30 edges
9. `Journal` - 29 edges
10. `DatabaseKeyWriter` - 28 edges

## Surprising Connections (you probably didn't know these)
- `SqlBetweenExpression` --references--> `SqlExpression`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlBetweenExpression.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlExpression.cs
- `SqlCastExpression` --references--> `SqlExpression`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlCastExpression.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlExpression.cs
- `SqlColumnReferenceExpression` --inherits--> `SqlExpression`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlColumnReferenceExpression.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlExpression.cs
- `SqlIsNullExpression` --references--> `SqlExpression`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlIsNullExpression.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlExpression.cs
- `SqlLikeExpression` --references--> `SqlExpression`  [EXTRACTED]
  resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlLikeExpression.cs → resources/Database/Assimalign.Cohesion.Database.Sql.Language/src/Expressions/SqlExpression.cs

## Import Cycles
- None detected.

## Communities (98 total, 23 thin omitted)

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
Cohesion: 0.17
Nodes (5): Assimalign.Cohesion.Database.Transactions, Version, LockManager, TransactionManager, VersionStore

### Community 5 - "StorageBufferPool"
Cohesion: 0.24
Nodes (6): Action, Func, IReadOnlyList, DatabaseKeyEncodingTests, DatabaseKeyWriter, Fact

### Community 6 - "BufferEntry"
Cohesion: 0.23
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.18
Nodes (6): IStoragePageManager, StoragePageManager, CancellationToken, IStoragePageHandle, PageId, ValueTask

### Community 8 - "Crc32"
Cohesion: 0.16
Nodes (8): Assimalign.Cohesion.Database.Storage.Internal, Crc32, ReadOnlySpan, uint, PageChecksum, PageId, ReadOnlySpan, Span

### Community 9 - "Assimalign.Cohesion.Database.Storage — Design"
Cohesion: 0.10
Nodes (18): AOT posture, Assimalign.Cohesion.Database.Storage — Design, Checkpoints, Design intent, Error model, Non-goals, Recovery replay rules, The buffer pool (+10 more)

### Community 10 - "4. The work items (with blockers)"
Cohesion: 0.14
Nodes (13): 1. How to run this across multiple sessions (read first), 2. Stages (dependency gates), 3. Lanes (what can run in parallel) + per-lane guardrails, 4. The work items (with blockers), 5. Progress Log (orchestrator-reconciled from merged PRs), 6. Fast reference, Database Program Plan (Data Platform, L03.02), MVP definition (what "done" means for the first cut) (+5 more)

### Community 11 - "StorageFreeSpaceMap"
Cohesion: 0.15
Nodes (12): bool, int, QueryParser, ReadOnlySpan, SqlQueryParser, QueryStatement, TokenLexer, SqlQueryParser (+4 more)

### Community 12 - "Assimalign.Cohesion.Database.Indexing — Design"
Cohesion: 0.20
Nodes (9): AOT posture, Assimalign.Cohesion.Database.Indexing — Design, Byte-comparable keys, Entry references are opaque `ulong`s, Intent, Non-goals, Relationship to `Database.Storage`, The B+Tree implementation (+1 more)

### Community 13 - "StorageFileHeader"
Cohesion: 0.14
Nodes (18): Assimalign.Cohesion.Database.Execution.Tests, IQueryPipelineStage, IQueryTransactionScope, QueryRequest, DiagnosticStage, RecordingStage, ShortCircuitStage, TestExpression (+10 more)

### Community 15 - "byte"
Cohesion: 0.05
Nodes (24): IJournal, IReadOnlyList, PageId, ReadOnlySpan, Journal, bool, byte, IEnumerable (+16 more)

### Community 16 - "int"
Cohesion: 0.16
Nodes (9): IDisposable, SqlStorage, PageId, ReadOnlyMemory, ReadOnlySpan, SlotIndex, StorageModel, Stream (+1 more)

### Community 17 - "IStoragePageHandle"
Cohesion: 0.15
Nodes (12): TransactionLog, IJournal, ITransactionLog, ILockManager, ITransactionLog, ITransactionManager, TransactionRecovery, TransactionRecoveryPlan (+4 more)

### Community 18 - "PageId"
Cohesion: 0.20
Nodes (8): DatabaseName, IDatabaseEngine, ISqlDatabase, SqlDatabaseInstance, bool, CancellationToken, IDatabaseSession, ValueTask

### Community 20 - "byte"
Cohesion: 0.35
Nodes (7): byte, InMemoryTransactionLog, CancellationToken, List, object, TransactionSequence, ValueTask

### Community 21 - "Dictionary"
Cohesion: 0.25
Nodes (8): Dictionary, IDictionary, QueryExecutionContext, CancellationToken, Diagnostic, IReadOnlyList, List, QueryRequest

### Community 23 - "int"
Cohesion: 0.07
Nodes (27): SqlAlterTableExpression, SqlCreateTableExpression, IReadOnlyList, SqlDeleteExpression, SqlDropTableExpression, SqlExistsExpression, SqlInExpression, IReadOnlyList (+19 more)

### Community 31 - "SeekOrigin"
Cohesion: 0.19
Nodes (10): IIndexManager, IndexDefinition, DefaultIndexManager, CancellationToken, Dictionary, IIndex, IReadOnlyList, ITransactionContext (+2 more)

### Community 34 - "Assimalign.Cohesion.Resources.slnx"
Cohesion: 0.02
Nodes (92): Assimalign.Cohesion.Configuration, Assimalign.Cohesion.Connections.Quic, Assimalign.Cohesion.Connections.Security, Assimalign.Cohesion.Connections.Tcp, Assimalign.Cohesion.DependencyInjection, Assimalign.Cohesion.FileSystem, Assimalign.Cohesion.Http.Connections, Assimalign.Cohesion.Http.Cookies (+84 more)

### Community 35 - "Assimalign.Cohesion.Database.slnx"
Cohesion: 0.02
Nodes (90): Assimalign.Cohesion.ApplicationModel, Assimalign.Cohesion.Connections, Assimalign.Cohesion.Core, Assimalign.Cohesion.Hosting, Assimalign.Cohesion.Database.ApplicationModel.Tests, Assimalign.Cohesion.Database.Blob.Catalog, Assimalign.Cohesion.Database.Blob.Catalog.Tests, Assimalign.Cohesion.Database.Blob.Client (+82 more)

### Community 36 - "DatabaseKeyReader"
Cohesion: 0.17
Nodes (10): DatabaseType, DatabaseKeyReader, DateOnly, DateTime, DateTimeOffset, Guid, int, ReadOnlySpan (+2 more)

### Community 37 - "DatabaseKeyWriter"
Cohesion: 0.13
Nodes (10): DatabaseKeyWriter, byte, DateOnly, DateTime, DateTimeOffset, Guid, int, ReadOnlySpan (+2 more)

### Community 38 - "DatabaseKeyEncodingTests"
Cohesion: 0.25
Nodes (9): Locks, Manager, TransactionManagerTests, Fact, ILockManager, ITransactionManager, IVersionStore, Task (+1 more)

### Community 39 - "StorageBufferPool"
Cohesion: 0.06
Nodes (26): DatabaseException, DatabaseKeyWriter, DatabaseTypeInfo, IComparable, IEquatable, IIndexCursor, IndexUniqueViolationException, IndexKey (+18 more)

### Community 40 - "Fact"
Cohesion: 0.11
Nodes (11): Memory, StorageRecovery, StorageStream, CancellationToken, PageId, ReadOnlyMemory, ReadOnlySpan, SeekOrigin (+3 more)

### Community 41 - "Assimalign.Cohesion.Database.Types — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Types — Design, Design intent, Error model, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Types — Overview, Dependencies (+2 more)

### Community 42 - "BufferEntry"
Cohesion: 0.06
Nodes (41): Fact, Harness, HashSet, Index, IStorageFreeSpaceMap, IStorageTransactionSource, ITransactionManager, Queue (+33 more)

### Community 43 - ".DisposeAsync"
Cohesion: 0.06
Nodes (33): IJournal, ValueTask, StorageTransaction, bool, Dictionary, Storage, bool, Dictionary (+25 more)

### Community 44 - "StorageCorruptionException"
Cohesion: 0.25
Nodes (5): JournalException, StorageCorruptionException, PageId, StorageTransactionException, StorageException

### Community 45 - "StorageFileHeader"
Cohesion: 0.25
Nodes (6): StorageFileHeader, byte, int, long, MethodImpl, StorageModel

### Community 46 - "Assimalign.Cohesion.Database.Sql.Internal"
Cohesion: 0.14
Nodes (5): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Storage.Units, Assimalign.Cohesion.Database.Storage.Tests, PageFlags, PageType

### Community 47 - ".FlushAll"
Cohesion: 0.07
Nodes (32): ITransactionContext, IVersionStore, CancellationToken, ReadOnlyMemory, TransactionSequence, TransactionSnapshot, ValueTask, DefaultTransactionContext (+24 more)

### Community 48 - "Assimalign.Cohesion.Database.Types"
Cohesion: 0.13
Nodes (10): Assimalign.Cohesion.Database.Transactions, Assimalign.Cohesion.Database.Indexing, IIndexRegistry, IReadOnlyList, BTreeIndexManager, IIndexManager, BTreeIndexManagerOptions, ILockManager (+2 more)

### Community 50 - "DefaultLockManager"
Cohesion: 0.15
Nodes (16): ILockManager, LockEntry, LockMode, LockResource, DefaultLockManager, LockEntry, Waiter, bool (+8 more)

### Community 51 - "StorageTransaction"
Cohesion: 0.17
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
Cohesion: 0.20
Nodes (6): SqlExecutionPipelineTests, Fact, SqlQueryRequest, string, Task, SqlDatabaseEngine

### Community 56 - ".InsertRecord"
Cohesion: 0.17
Nodes (8): IQueryPipelineStage, CancellationToken, QueryPipelineDelegate, QueryResult, ValueTask, QueryPipelineBuilder, List, QueryPipelineDelegate

### Community 57 - "SqlStorage"
Cohesion: 0.16
Nodes (4): SqlExpressionParserTests, Fact, SqlExpression, SqlQueryParser

### Community 58 - ".ExecuteAsync"
Cohesion: 0.47
Nodes (4): VersionStoreTests, Fact, Task, TransactionSnapshot

### Community 59 - "Assimalign.Cohesion.Database.Execution — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Execution — Design, Design intent, Lifecycle pattern, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Execution — Overview, Dependencies (+2 more)

### Community 60 - "IStorage"
Cohesion: 0.06
Nodes (39): EntryReference, IIndex, IndexKind, Promoted, ReaderWriterLockSlim, IStorageTransactionSource, IStorageTransaction, ITransactionContext (+31 more)

### Community 61 - ".ExecuteAsync"
Cohesion: 0.32
Nodes (5): BuiltQueryPipeline, CancellationToken, QueryPipelineDelegate, QueryResult, ValueTask

### Community 63 - "IQueryTransactionScope"
Cohesion: 0.38
Nodes (4): IAsyncDisposable, IQueryTransactionScope, CancellationToken, ValueTask

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
Nodes (4): QueryStatementResult, Diagnostic, IReadOnlyList, QueryResultStatus

### Community 68 - "Assimalign.Cohesion.Database.Execution.csproj"
Cohesion: 0.06
Nodes (19): Assimalign.Cohesion.Database.Language, Assimalign.Cohesion.Database.Sql.Tests, Assimalign.Cohesion.Database.Sql.Language, Microsoft.NET.Sdk, SqlBetweenExpression, SqlBinaryExpression, SqlCastExpression, SqlColumnReferenceExpression (+11 more)

### Community 70 - "FailingCommitLog"
Cohesion: 0.26
Nodes (6): Assimalign.Cohesion.Database.Transactions.Tests, ITransactionLog, FailingCommitLog, CancellationToken, TransactionSequence, ValueTask

### Community 71 - "JournalTransactionLog"
Cohesion: 0.39
Nodes (5): JournalTransactionLog, CancellationToken, IJournal, TransactionSequence, ValueTask

### Community 72 - "BTreeIndexRegistration"
Cohesion: 0.47
Nodes (3): SqlExpression, SqlQueryParser, TokenLexer

### Community 73 - ".Create"
Cohesion: 0.11
Nodes (17): AOT posture, Assimalign.Cohesion.Database.Sql.Language — Design, Design intent, Namespace note, Non-goals (current dialect), Why-this-not-that decisions, Builtin functions, Diagnostics (+9 more)

### Community 74 - "SqlSelectParserTests"
Cohesion: 0.20
Nodes (3): SqlSelectParserTests, Fact, SqlQueryParser

### Community 75 - "StorageBufferPool"
Cohesion: 0.23
Nodes (8): BufferEntry, IStorageBufferPool, LinkedList, StorageBufferPool, Dictionary, object, PageId, Stack

### Community 76 - ".IsKeyword"
Cohesion: 0.28
Nodes (6): SqlQueryParser, TokenLexer, SqlQueryParser, List, TokenLexer, SqlSelectColumn

### Community 78 - ".ExecuteAsync"
Cohesion: 0.30
Nodes (9): CancellationToken, IQueryExecutor, IStorageTransaction, QueryResult, SqlQueryExecutor, QueryRequest, SqlQueryRequest, Task (+1 more)

### Community 79 - "BufferEntry"
Cohesion: 0.15
Nodes (8): GCHandle, LinkedListNode, Page, BufferEntry, bool, byte, int, StorageStream

### Community 80 - "SqlDatabaseSession"
Cohesion: 0.23
Nodes (9): IDatabase, IDatabaseSession, SqlDatabaseSession, CancellationToken, IDatabaseTransaction, QueryRequest, QueryResult, ValueTask (+1 more)

### Community 81 - "SqlDdlParserTests"
Cohesion: 0.31
Nodes (3): SqlDdlParserTests, Fact, SqlQueryParser

### Community 82 - "SqlDurabilityTests"
Cohesion: 0.35
Nodes (5): SqlDurabilityTests, Fact, SqlQueryRequest, string, Task

### Community 83 - "Assimalign.Cohesion.Database.Sql.Language.Tests"
Cohesion: 0.20
Nodes (3): Assimalign.Cohesion.Database.Sql.Language.Tests, SqlTokenLexerTest, Fact

### Community 84 - "SqlQueryParserTests"
Cohesion: 0.24
Nodes (5): SqlQueryParserTests, Fact, InlineData, SqlQueryCommandType, Theory

### Community 85 - "SqlQueryStatement"
Cohesion: 0.22
Nodes (8): Assimalign.Cohesion.Database.Sql, IReadOnlyDictionary, QueryExpression, QueryStatement, TestStatement, SqlQueryStatement, QueryExpression, SqlQueryRequest

### Community 86 - "SqlDatabaseTransaction"
Cohesion: 0.33
Nodes (6): IDatabaseTransaction, SqlDatabaseTransaction, CancellationToken, ValueTask, TransactionId, TransactionState

### Community 87 - ".ParseInsert"
Cohesion: 0.54
Nodes (4): SqlQueryParser, IReadOnlyList, List, TokenLexer

### Community 88 - "SqlInsertParserTests"
Cohesion: 0.39
Nodes (3): SqlInsertParserTests, Fact, SqlQueryParser

### Community 89 - "SqlDeleteParserTests"
Cohesion: 0.38
Nodes (3): SqlDeleteParserTests, Fact, SqlQueryParser

### Community 91 - "SqlUpdateParserTests"
Cohesion: 0.47
Nodes (3): SqlUpdateParserTests, Fact, SqlQueryParser

### Community 94 - "SqlCaseExpression"
Cohesion: 0.50
Nodes (3): SqlCaseExpression, IReadOnlyList, SqlWhenClause

## Knowledge Gaps
- **310 isolated node(s):** `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails`, `Stage 1 — Kernel`, `Stage 2 — Kernel build-out + languages (all language items are parallel-safe from day one)` (+305 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **23 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Assimalign.Cohesion.Database.Language` connect `Assimalign.Cohesion.Database.Execution.csproj` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `QueryStatementResult`, `Assimalign.Cohesion.Database.slnx`, `StorageBufferPool`, `StorageFileHeader`, `Assimalign.Cohesion.Database.Sql.Language.Tests`, `Assimalign.Cohesion.Database.Execution`?**
  _High betweenness centrality (0.262) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Transactions` connect `Assimalign.Cohesion.Database.Types` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `Assimalign.Cohesion.Database.slnx`, `BTreeIndexTests.cs`?**
  _High betweenness centrality (0.203) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Storage` connect `Assimalign.Cohesion.Database.Sql.Internal` to `Fact`, `BufferEntry`, `.DisposeAsync`, `StorageCorruptionException`, `StorageFileHeader`, `IStorage`, `byte`, `Assimalign.Cohesion.Database.Types`, `int`, `BufferEntry`, `Assimalign.Cohesion.Database.Sql.Internal`, `BTreeIndexTests.cs`?**
  _High betweenness centrality (0.202) - this node is a cross-community bridge._
- **What connects `1. How to run this across multiple sessions (read first)`, `2. Stages (dependency gates)`, `3. Lanes (what can run in parallel) + per-lane guardrails` to the rest of the system?**
  _310 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `StorageModelAlignmentTests` be split into smaller, more focused modules?**
  _Cohesion score 0.008888888888888889 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10582010582010581 - nodes in this community are weakly interconnected._
- **Should `StorageStream` be split into smaller, more focused modules?**
  _Cohesion score 0.07562008469449485 - nodes in this community are weakly interconnected._