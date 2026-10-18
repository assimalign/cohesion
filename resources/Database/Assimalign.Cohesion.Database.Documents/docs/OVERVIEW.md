# Documents engine

`Assimalign.Cohesion.Database.Documents` implements named logical databases containing
versioned UTF-8 JSON documents. Each session is bound to one database. The engine
supports document CRUD, OQL queries, KEY/VALUE INSERT and UPSERT, SET/UNSET UPDATE, DELETE and collection/index DDL, secondary B+Tree indexes,
snapshot and read-committed transactions, durable file or in-memory storage, and a wire server.

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
defines the supported grammar; the [storage design](../../Assimalign.Cohesion.Database.Documents.Storage/docs/DESIGN.md)
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
