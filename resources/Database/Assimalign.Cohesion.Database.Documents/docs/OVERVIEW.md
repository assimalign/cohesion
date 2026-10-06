# Documents engine

`Assimalign.Cohesion.Database.Documents` implements named logical databases containing
versioned UTF-8 JSON documents. Each session is bound to one database. The engine
supports document CRUD, an explicit OQL query and index-DDL subset, secondary B+Tree indexes,
snapshot and read-committed transactions, and durable file or in-memory storage.

The public entry point is `DocumentDatabaseEngine.Create(options)`. A created database is a
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

A failed journal or data fsync takes the database offline: every later operation is refused
with `DatabaseOfflineException` (`COHDBD002`) until `OpenDatabaseAsync` reopens it and recovery
decides the unconfirmed commit (#1243). `BufferPoolCapacity` (32 MiB), `CheckpointJournalSize`
(256 MiB) and `CheckpointInterval` (5 minutes) size the buffer pool and trigger checkpoints
(#1254); a failed undo is retried on a 100 ms backoff (#1226). See DESIGN.md, "Storage
operations".

```csharp
await using var engine = DocumentDatabaseEngine.Create(new());
var database = await engine.CreateDatabaseAsync("shop");
await using var session = await database.CreateSessionAsync();
var orders = await session.CreateCollectionAsync("orders");
await orders.PutAsync(session, "order/1", "{\"customer\":{\"name\":\"Ada\"},\"total\":42}"u8.ToArray());
await session.ExecuteAsync("CREATE INDEX by_total ON orders (total)");
var result = await session.ExecuteAsync("SELECT o.customer.name FROM orders o WHERE o.total >= 40");
```

`AddDocuments((context, engine) => ...)` captures engine construction on the root
`IDatabaseApplicationBuilder` and returns that application builder. During Build,
the callback configures the sealed `DocumentDatabaseEngineBuilder`, including deferred worker
and server factories typed over `DocumentDatabaseEngine`. The application owns the resulting
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

`DocumentDatabaseEngine.CreateBuilder()` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.
