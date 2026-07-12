# Graph Report - confident-lederberg-3df082  (2026-07-11)

## Corpus Check
- 4135 files · ~1,228,545 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 2087 nodes · 4188 edges · 101 communities (82 shown, 19 thin omitted)
- Extraction: 92% EXTRACTED · 8% INFERRED · 0% AMBIGUOUS · INFERRED: 342 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `fb01c91a`
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
- .ParseUpdate
- Assimalign.Cohesion.Database.Sql.Catalog
- Assimalign.Cohesion.Database.Sql.Catalog.Tests

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
10. `DefaultSqlCatalog` - 28 edges

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

## Communities (101 total, 19 thin omitted)

### Community 1 - "StorageModelAlignmentTests"
Cohesion: 0.01
Nodes (225): Assimalign.Cohesion.SourceGeneration, Assimalign.Cohesion.Build.Tasks, Assimalign.Cohesion.ProjectTemplates, Assimalign.Cohesion, Assimalign.Cohesion.App.ApiManager.Refs, Assimalign.Cohesion.App.ApiManager.Runtime, Assimalign.Cohesion.App.ConfigurationStore.Refs, Assimalign.Cohesion.App.ConfigurationStore.Runtime (+217 more)

### Community 2 - "SlottedPage"
Cohesion: 0.11
Nodes (15): IStorageUnit, IStorageUnitIterator, PageSlot, StorageUnitIterator, int, IStorageFreeSpaceMap, IStoragePageHandle, IStoragePageManager (+7 more)

### Community 3 - "StorageStream"
Cohesion: 0.06
Nodes (25): Assimalign.Cohesion.Database.Storage.Tests.TestObjects, HarnessStorage, MemoryStream, CrashSimulationStream, bool, byte, HarnessStorage, StorageModel (+17 more)

### Community 4 - "Assimalign.Cohesion.Database.Storage"
Cohesion: 0.11
Nodes (8): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Transactions, Version, LockManager, TransactionLog, ITransactionLog, TransactionManager, VersionStore

### Community 5 - "StorageBufferPool"
Cohesion: 0.26
Nodes (5): Func, IReadOnlyList, DatabaseKeyEncodingTests, DatabaseKeyWriter, Fact

### Community 6 - "BufferEntry"
Cohesion: 0.23
Nodes (10): PageFlags, Header, Page, byte, int, long, PageType, Span (+2 more)

### Community 7 - "StoragePageManager"
Cohesion: 0.18
Nodes (7): IStoragePageManager, StoragePageManager, CancellationToken, IStoragePageHandle, PageId, ValueTask, PageType

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
Cohesion: 0.17
Nodes (11): bool, QueryParser, SqlQueryParser, TokenLexer, SqlQueryParser, TokenLexer, SqlQueryParser, List (+3 more)

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
Cohesion: 0.19
Nodes (8): IDisposable, SqlStorage, PageId, ReadOnlyMemory, ReadOnlySpan, SlotIndex, StorageModel, IStorageTransaction

### Community 17 - "IStoragePageHandle"
Cohesion: 0.19
Nodes (10): IJournal, ILockManager, ITransactionLog, ITransactionManager, TransactionRecovery, TransactionRecoveryPlan, IJournal, TransactionRecoveryTests (+2 more)

### Community 18 - "PageId"
Cohesion: 0.20
Nodes (8): DatabaseName, IDatabaseEngine, ISqlDatabase, SqlDatabaseInstance, bool, CancellationToken, IDatabaseSession, ValueTask

### Community 19 - "JournalTests"
Cohesion: 0.07
Nodes (17): Assimalign.Cohesion.Database.Storage.Tests, HashSet, IStorageFreeSpaceMap, Queue, StorageFreeSpaceMap, long, PageId, JournalTests (+9 more)

### Community 20 - "byte"
Cohesion: 0.35
Nodes (7): byte, InMemoryTransactionLog, CancellationToken, List, object, TransactionSequence, ValueTask

### Community 21 - "Dictionary"
Cohesion: 0.25
Nodes (8): Dictionary, IDictionary, QueryExecutionContext, CancellationToken, Diagnostic, IReadOnlyList, List, QueryRequest

### Community 23 - "int"
Cohesion: 0.06
Nodes (28): ReadOnlySpan, SqlAlterTableExpression, SqlCreateTableExpression, IReadOnlyList, SqlDeleteExpression, SqlDropTableExpression, SqlExistsExpression, SqlInExpression (+20 more)

### Community 26 - "Name"
Cohesion: 0.07
Nodes (28): Assimalign.Cohesion.Database.Sql.Storage, Assimalign.Cohesion.Database.Sql.Catalog, DatabaseKeyReader, IEqualityComparer, int, Name, object, PageId (+20 more)

### Community 27 - "object"
Cohesion: 0.20
Nodes (9): IStoragePageHandle, IStorageTransaction, PageId, PageType, ReadOnlyMemory, ReadOnlySpan, SlotIndex, SlottedPage (+1 more)

### Community 28 - "PageId"
Cohesion: 0.11
Nodes (14): IStorageTransaction, IStorage, IStorageBufferPool, IStorageFreeSpaceMap, IStoragePageHandle, IStoragePageManager, IStorageTransaction, IStorageUnitIterator (+6 more)

### Community 31 - "SeekOrigin"
Cohesion: 0.11
Nodes (16): IIndexManager, IndexDefinition, IIndexRegistry, IReadOnlyList, BTreeIndexManagerOptions, ILockManager, IReadOnlyList, BTreeIndexRegistration (+8 more)

### Community 34 - "Assimalign.Cohesion.Resources.slnx"
Cohesion: 0.02
Nodes (94): Assimalign.Cohesion.Configuration, Assimalign.Cohesion.Connections.Quic, Assimalign.Cohesion.Connections.Security, Assimalign.Cohesion.Connections.Tcp, Assimalign.Cohesion.DependencyInjection, Assimalign.Cohesion.FileSystem, Assimalign.Cohesion.Http.Connections, Assimalign.Cohesion.Http.Cookies (+86 more)

### Community 35 - "Assimalign.Cohesion.Database.slnx"
Cohesion: 0.02
Nodes (93): Assimalign.Cohesion.ApplicationModel, Assimalign.Cohesion.Connections, Assimalign.Cohesion.Core, Assimalign.Cohesion.Hosting, Assimalign.Cohesion.Database.ApplicationModel, Assimalign.Cohesion.Database.ApplicationModel.Tests, Assimalign.Cohesion.Database.Blob.Catalog, Assimalign.Cohesion.Database.Blob.Catalog.Tests (+85 more)

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
Cohesion: 0.05
Nodes (27): DatabaseException, DatabaseKeyWriter, DatabaseTypeInfo, IComparable, IEquatable, IIndexCursor, IndexUniqueViolationException, IndexKey (+19 more)

### Community 40 - "Fact"
Cohesion: 0.10
Nodes (12): Memory, Stream, StorageRecovery, StorageStream, CancellationToken, PageId, ReadOnlyMemory, ReadOnlySpan (+4 more)

### Community 41 - "Assimalign.Cohesion.Database.Types — Design"
Cohesion: 0.17
Nodes (10): AOT posture, Assimalign.Cohesion.Database.Types — Design, Design intent, Error model, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Types — Overview, Dependencies (+2 more)

### Community 42 - "BufferEntry"
Cohesion: 0.08
Nodes (34): Catalog, CatalogHarness, Assimalign.Cohesion.Database.Sql.Catalog.Tests, Fact, Harness, Index, IStorageTransactionSource, ITransactionManager (+26 more)

### Community 43 - ".DisposeAsync"
Cohesion: 0.08
Nodes (20): IJournal, ValueTask, Storage, bool, Dictionary, IStorageBufferPool, IStorageFreeSpaceMap, IStoragePageManager (+12 more)

### Community 44 - "StorageCorruptionException"
Cohesion: 0.25
Nodes (5): JournalException, StorageCorruptionException, PageId, StorageTransactionException, StorageException

### Community 45 - "StorageFileHeader"
Cohesion: 0.25
Nodes (6): StorageFileHeader, byte, int, long, MethodImpl, StorageModel

### Community 46 - "Assimalign.Cohesion.Database.Sql.Internal"
Cohesion: 0.18
Nodes (3): Assimalign.Cohesion.Database.Storage, Assimalign.Cohesion.Database.Storage.Units, PageFlags

### Community 47 - ".FlushAll"
Cohesion: 0.07
Nodes (32): ITransactionContext, IVersionStore, CancellationToken, ReadOnlyMemory, TransactionSequence, TransactionSnapshot, ValueTask, DefaultTransactionContext (+24 more)

### Community 48 - "Assimalign.Cohesion.Database.Types"
Cohesion: 0.16
Nodes (6): Assimalign.Cohesion.Database.Transactions, Assimalign.Cohesion.Database.Indexing.Tests, Assimalign.Cohesion.Database.Indexing, Assimalign.Cohesion.Database.Indexing.Tests.TestObjects, BTreeIndexManager, IIndexManager

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
Cohesion: 0.22
Nodes (6): Promoted, List, BTreeNode, byte, int, SiblingId

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
Nodes (17): Assimalign.Cohesion.Database.Language, Assimalign.Cohesion.Database.Sql.Language, SqlBetweenExpression, SqlBinaryExpression, SqlCastExpression, SqlColumnReferenceExpression, SqlFunctionCallExpression, IReadOnlyList (+9 more)

### Community 70 - "FailingCommitLog"
Cohesion: 0.26
Nodes (6): Assimalign.Cohesion.Database.Transactions.Tests, ITransactionLog, FailingCommitLog, CancellationToken, TransactionSequence, ValueTask

### Community 71 - "JournalTransactionLog"
Cohesion: 0.46
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
Cohesion: 0.20
Nodes (9): Action, BufferEntry, IStorageBufferPool, LinkedList, StorageBufferPool, Dictionary, object, PageId (+1 more)

### Community 76 - ".IsKeyword"
Cohesion: 0.18
Nodes (11): EntryReference, IIndex, IndexKind, ReaderWriterLockSlim, BTreeIndex, IIndexCursor, ILockManager, IndexKeyRange (+3 more)

### Community 77 - ".Pin"
Cohesion: 0.19
Nodes (3): IStoragePageHandle, StorageBufferPoolTests, StorageStream

### Community 78 - ".ExecuteAsync"
Cohesion: 0.10
Nodes (24): CancellationToken, IDatabase, IDatabaseSession, IDatabaseTransaction, IQueryExecutor, IStorageTransaction, QueryResult, SqlDatabaseSession (+16 more)

### Community 79 - "BufferEntry"
Cohesion: 0.22
Nodes (7): GCHandle, LinkedListNode, Page, BufferEntry, bool, byte, int

### Community 80 - "SqlDatabaseSession"
Cohesion: 0.21
Nodes (4): StorageTransaction, bool, Dictionary, StorageTransaction

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
Cohesion: 0.14
Nodes (10): Assimalign.Cohesion.Database.Sql.Tests, Assimalign.Cohesion.Database.Sql, IReadOnlyDictionary, QueryExpression, QueryStatement, Microsoft.NET.Sdk, TestStatement, SqlQueryStatement (+2 more)

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

### Community 92 - "BTreeIndexTests.cs"
Cohesion: 0.18
Nodes (9): AOT posture, Assimalign.Cohesion.Database.Sql.Catalog — Design, Error model, Non-goals, Why-this-not-that decisions, Assimalign.Cohesion.Database.Sql.Catalog — Overview, Dependencies, Scope (+1 more)

### Community 93 - "Assimalign.Cohesion.Database.Storage"
Cohesion: 0.29
Nodes (6): IStorageTransactionSource, IStorageTransaction, ITransactionContext, CancellationToken, ITransactionContext, ValueTask

### Community 94 - "SqlCaseExpression"
Cohesion: 0.50
Nodes (3): SqlCaseExpression, IReadOnlyList, SqlWhenClause

### Community 98 - ".ParseUpdate"
Cohesion: 0.36
Nodes (5): SqlUpdateExpression, IReadOnlyList, SqlAssignment, SqlQueryParser, TokenLexer

## Knowledge Gaps
- **323 isolated node(s):** `Assimalign.Cohesion.SourceGeneration`, `Assimalign.Cohesion.Build.Tasks`, `Assimalign.Cohesion.ProjectTemplates`, `Assimalign.Cohesion`, `Assimalign.Cohesion.App.ApiManager.Refs` (+318 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **19 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Assimalign.Cohesion.Database.Language` connect `Assimalign.Cohesion.Database.Execution.csproj` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `QueryStatementResult`, `Assimalign.Cohesion.Database.slnx`, `StorageBufferPool`, `StorageFileHeader`, `Assimalign.Cohesion.Database.Sql.Language.Tests`, `SqlQueryStatement`, `Assimalign.Cohesion.Database.Execution`?**
  _High betweenness centrality (0.205) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Transactions` connect `Assimalign.Cohesion.Database.Types` to `StorageModelAlignmentTests`, `Assimalign.Cohesion.Resources.slnx`, `Assimalign.Cohesion.Database.slnx`?**
  _High betweenness centrality (0.168) - this node is a cross-community bridge._
- **Why does `Assimalign.Cohesion.Database.Storage` connect `Assimalign.Cohesion.Database.Sql.Internal` to `StoragePageManager`, `Fact`, `StorageCorruptionException`, `StorageFileHeader`, `byte`, `Assimalign.Cohesion.Database.Types`, `int`, `BufferEntry`, `JournalTests`, `SqlDatabaseSession`, `Assimalign.Cohesion.Database.Sql.Internal`, `PageId`?**
  _High betweenness centrality (0.148) - this node is a cross-community bridge._
- **What connects `Assimalign.Cohesion.SourceGeneration`, `Assimalign.Cohesion.Build.Tasks`, `Assimalign.Cohesion.ProjectTemplates` to the rest of the system?**
  _323 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `StorageModelAlignmentTests` be split into smaller, more focused modules?**
  _Cohesion score 0.008849557522123894 - nodes in this community are weakly interconnected._
- **Should `SlottedPage` be split into smaller, more focused modules?**
  _Cohesion score 0.10582010582010581 - nodes in this community are weakly interconnected._
- **Should `StorageStream` be split into smaller, more focused modules?**
  _Cohesion score 0.06442307692307692 - nodes in this community are weakly interconnected._