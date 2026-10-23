# Documents engine

## Wire contract ownership

`Assimalign.Cohesion.Database.Documents.Protocol` owns this model's wire messages, codecs, family singleton and transferred value shapes (14 public types). Both engine and remote client reference that one assembly. Its public namespace and RootNamespace equal its project name. Engine requests, planning, catalog, storage and server lifecycle stay in `Assimalign.Cohesion.Database.Documents`. This extraction preserves wire bytes and validation; moved public types intentionally change namespace and assembly identity with no forwarders.


`Assimalign.Cohesion.Database.Documents` implements named logical databases containing
versioned UTF-8 JSON documents. Each session is bound to one database. The engine
supports document CRUD, OQL queries, KEY/VALUE INSERT and UPSERT, SET/UNSET UPDATE, DELETE and collection/index DDL, secondary B+Tree indexes,
snapshot and read-committed transactions, durable file or in-memory storage, and a wire server.

OQL executes 34 SQL++ scalar functions, including Unicode string operations, exact math,
UTC date conversion, type/array tests and short-circuit conditionals. Clock functions share the
transaction-start instant. COUNT and string/array lengths and positions return Int64 long values,
with matching wire column types; the language [dialect](../../Assimalign.Cohesion.Database.Documents.Language/docs/DIALECT.md)
records the full contract. LIMIT/OFFSET page stored and system queries after grouping, DISTINCT
and ordering. Counts accept literals or parameters through Int64.MaxValue; invalid values carry
OQL0007. Column types remain independent of the page, including empty results.
INNER/LEFT UNNEST expands array elements into scoped rows before WHERE, grouping and paging.
ANY/SOME/EVERY and array-value IN query arrays without expansion. A star with UNNEST returns
synthetic objects keyed by variable names; an ordinary star preserves the stored document.

`DocumentDatabaseServer.Create(engine, options)` or `engineBuilder.AddServer(options => ...)`
serves OQL and document/collection commands on a consumer-supplied listener. Each authenticated
connection binds one database. [Documents.Client](../../Assimalign.Cohesion.Database.Documents.Client/docs/OVERVIEW.md)
provides pooled concrete client APIs, whole-document and row results, versioned mutations and
structured diagnostics. Statement failures leave the connection reusable; explicit transactions
remain in process only.

The public entry points are `DocumentDatabaseEngine.Create(name, options)` and the builder from
`DocumentDatabaseEngine.CreateBuilder(name)`; no options type carries the engine name. A created database is a
`DocumentDatabase`; open a `DocumentDatabaseSession`, create or get a collection through it, and
use the `DocumentCollection` methods. Collection operations exist only on the session
(`session.CreateCollectionAsync` and its siblings; owner decision 32): they run in the session's
transaction, or in an automatic statement transaction when none is active. Disposing a database,
directly or through `session.Database`, closes it for every session; once the close ends the
engine forgets it, and `OpenDatabaseAsync` opens it again with its documents, in memory as on
disk (owner decision 33). Index definitions are changed with OQL on the session, using its
active transaction or an automatic statement transaction; neither the database nor the session
has index-management members. A statement that fails inside an explicit transaction aborts the
whole transaction: the session refuses further statements and BEGIN with `COHDBD001` until the
caller rolls back, and a commit fails without committing.

OQL INSERT creates externally keyed documents from VALUES pairs or SELECT output names and
reports the number inserted. META([alias]).id and .version read stored identity and version;
missing object members are omitted and explicit NULL members are stored. Duplicates fail the
whole statement with DocumentDuplicateKeyException (COHDOC012).

OQL UPSERT shares INSERT's grammar and validation, inserting an absent key or replacing its
whole document with a fresh version. Repeated keys fail atomically. It reports written-document
count; EXPLAIN UPSERT describes the VALUES or SELECT plan without writing.

OQL UPDATE collection [AS? alias] accepts SET assignments and/or UNSET property paths, optionally
filtered by WHERE. It reads every value from the pre-update document, creates missing intermediate
objects, preserves property order and reports the number matched with fresh versions. MISSING
removes named properties and becomes null in array elements. Shape and overlapping-target failures
abort the statement; EXPLAIN UPDATE includes its chosen access path without writing.

OQL DELETE FROM collection [AS? alias] [WHERE predicate] reports the number deleted. It
reuses query index seeks, filters the statement snapshot before writing, and retains the
collection and its index definitions. EXPLAIN DELETE describes the access without writing.

OQL also creates and removes collections with `CREATE COLLECTION [IF NOT EXISTS] name` and
`DROP COLLECTION [IF EXISTS] name`. They share the C# collection operations' transaction and
ownership rules and return Success with affected count zero. `IF` remains usable as a name.

A failed journal or data fsync takes the database offline: every later operation is refused
with `DatabaseOfflineException` (`COHDBD002`) until `OpenDatabaseAsync` reopens it and recovery
decides the unconfirmed commit (#1243). `BufferPoolCapacity` (32 MiB), `CheckpointJournalSize`
(256 MiB) and `CheckpointInterval` (5 minutes) size the buffer pool and trigger checkpoints
(#1254); a failed undo is retried on a 100 ms backoff (#1226). See DESIGN.md, "Storage
operations".

```csharp
await using var engine = DocumentDatabaseEngine.Create("shop-documents", new());
var database = await engine.CreateDatabaseAsync("shop");
await using var session = await database.CreateSessionAsync();
var orders = await session.CreateCollectionAsync("orders");
await orders.PutAsync(session, "order/1", "{\"customer\":{\"name\":\"Ada\"},\"total\":42}"u8.ToArray());
await session.ExecuteAsync("CREATE INDEX by_total ON orders (total)");
var result = await session.ExecuteAsync("SELECT o.customer.name FROM orders o WHERE o.total >= 40");
```

`AddDocuments(name, engine => ...)` captures engine construction on the root
`IDatabaseApplicationBuilder` and returns that application builder. During Build,
the callback configures the sealed `DocumentDatabaseEngineBuilder`: its `Options` (copied at
build), the databases it declares (`AddDatabase(name)`: opened or created by the build, never
dropped while declared), and deferred worker and server factories typed over
`DocumentDatabaseEngine`. The application owns the resulting
engine and its nested components. The model has no Hosting dependency. Standalone Create
remains available; all four built-in workers start with engine creation and stop when the
engine is disposed.

The engine, database, session, transaction, collection and builder are sealed types; the first
four are leaves of the area root's bases (`DatabaseEngine`, `DatabaseInstance`,
`DatabaseSession`, `DatabaseTransaction`), which own the shared lifecycle, the
explicit-transaction state machine and their checks, so the typed members need no casts
(concrete-types plan, phase 4; DESIGN.md, "Concrete types").

The engine references the Documents Language, Catalog, and Storage packages and
the shared Database root. [DESIGN.md](DESIGN.md) describes transaction ownership,
OQL semantics, and limits. The [language design](../../Assimalign.Cohesion.Database.Documents.Language/docs/DESIGN.md)
defines the supported grammar; the [storage design](DESIGN.md#storage-component)
defines the on-disk format. ApplicationModel and compiled-schema provisioning
remain outside this composition change.

`DocumentDatabaseEngine.CreateBuilder(name)` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.

## Session execution (#1576, 2026-10-16)

`DocumentDatabaseSession` derives from `DatabaseLanguageSession<DocumentQueryRequest>`. It offers typed
and text execution; a foreign-model request fails compilation. `DocumentQueryRequest` owns its typed
`Statement` and optional `Parameters` over the memberless `DatabaseRequest` root. Results
use the model-neutral `DatabaseResult` family. Lifecycle and explicit transactions remain
on `DatabaseSession`.


## Catalog component

The Catalog component is part of `Assimalign.Cohesion.Database.Documents`, with public types in `src/Catalog/` and helpers in `src/Internal/Catalog/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Catalog: Documents catalog component

The `src/Catalog/` component supplies the sealed `DocumentCatalog`, over the
same `DocumentStorage` file set as document content. It stores collection
ownership, versioned document identities/content references, and secondary index definitions.
Its B+Trees come from `Database.Indexing`, and every data/index mutation joins the caller's
`TransactionContext` through `TransactionCoordinator`.

`DocumentCatalog.Open(storage, coordinator)` opens the metadata directory and persisted physical
trees. `DocumentCatalog.EnsureIndexFormat(storage)` checks, before the coordinator's recovery
scrub and without writing, that every persisted tree is in the B-tree page format this engine
reads, and throws `IndexFormatException` (`COHDBI001`) when one is not (#1194). Read methods
accept an explicit visibility snapshot. Mutation methods neither begin nor
commit the caller's transaction. The engine takes the logical database's exclusive writer lock,
rejects write conflicts and stale index definitions, and enforces schema ownership before calling
the catalog.

`CreateIndexAsync` builds a scalar-path index immediately. `SaveDocumentAsync` and
`DeleteDocumentAsync` maintain every visible index alongside metadata writes; queries do not
rebuild indexes. The Documents engine reaches index creation and deletion through planned OQL
`CREATE INDEX` and `DROP INDEX` statements; these catalog methods remain lower-level transactional
composition seams. `SearchIndexAsync` performs a B+Tree equality or range seek and resolves only
snapshot-visible document versions, ordered by ordinal identity. See [DESIGN.md](DESIGN.md) for
the format and the precise path/scalar semantics.


## Storage component

The Storage component is part of `Assimalign.Cohesion.Database.Documents`, with public types in `src/Storage/` and helpers in `src/Internal/Storage/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Storage: Documents storage component

The `src/Storage/` component stores immutable UTF-8 JSON content as chunk
chains over `Database.Storage`. `Database.Transactions` supplies the shared logical coordinator,
version stamps, undo ledger, and recovery classification. The package has no dependency on the
document engine, Hosting, ApplicationModel, or a serializer that discovers runtime types.

Create a file set with `DocumentStorage.Create`; construct a `TransactionCoordinator` using
`WriteAheadJournal` and `Records`. `WriteContentAsync` validates a document, writes its chunks in
the supplied transaction, and returns a `DocumentContentReference`. Publish that reference through
Documents.Catalog before committing. `ReadContent` validates integrity and returns the original
bytes. JSON objects, arrays, and scalar roots are supported, including mixed document shapes.

The streaming chunk helpers follow the Blob storage precedent. They transport bytes and require
their caller to validate document syntax before publication; the engine uses the validated memory
helper. Documents are currently materialized for query evaluation and limited to `Int32.MaxValue`
content bytes. This is a format/implementation boundary rather than an eager allocation promise.

The original `InsertDocument`, `ReadDocument`, `UpdateDocument`, and `DeleteDocument` methods are
retained low-level, unstamped record helpers for existing consumers. They are not the engine's
document write path and must not be mixed into engine-owned file sets. See [DESIGN.md](DESIGN.md)
for the exact format, compatibility policy, and recovery order.

Offline refusals and unconfirmed commits expose their existing model code through Diagnostics,
with unprefixed exception detail and the original cause. Failure tracing reads that carrier;
legacy wire error text keeps its existing code prefix. See DESIGN, structured failure codes (#1350).

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

## Index history reclamation (#1195)

The shared coordinator's purge pass reclaims safe dead B-tree entries in bounded batches;
unique inserts also prune their key's history while reusing a single descent. Old snapshot
views remain valid. Empty-leaf reclamation remains deferred, with no format, wire or language
change. The callback follows the current manager after catalog/store reattachment.


## Confirmed decode failures (#1372)

Storage's D1 seqlock confirms private record copies before models decode them. Malformed
copies report the existing corruption code with page and slot. Direct catalog APIs retain
their model exception type and cause; engine boundaries use the common corruption exception. The shared version store rejects records shorter than its
16-byte stamp header and retains failed undo/prune work for retry. StorageCorruptionException
adds a record-scoped constructor and nullable SlotIndex. Formats and wire frames are unchanged.

Documents keeps the existing model refusal for a recognized unsupported catalog version;
its location is appended to the original format remedy. Current-format damage carries the
common corruption code. Both paths retain the original cause and refuse before recovery writes.

## Costed document access (#1527)

Competing scalar and composite index paths use bounded snapshot-visible entry probes. A rare
range can beat a common equality. Comparable complete inputs may intersect ordinal document
identities before reading JSON; full residual filtering, ordering, snapshot and mutation rules
remain intact. EXPLAIN names IndexSeek/IndexIntersection and labels entry estimates or bounds,
without executing the statement. There are no persisted statistics, ANALYZE command or format
changes. See DESIGN for probe budgets, eligibility thresholds and the limits of incomplete counts.