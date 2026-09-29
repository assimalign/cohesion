# Documents engine

`Assimalign.Cohesion.Database.Documents` implements named logical databases containing
versioned UTF-8 JSON documents. Each session is bound to one database. The engine
supports document CRUD, an explicit OQL query and index-DDL subset, secondary B+Tree indexes,
snapshot and read-committed transactions, and durable file or in-memory storage.

The public entry point is `DocumentDatabaseEngine.Create(options)`. Cast a created
database to `IDocumentDatabase`, create a collection, and use a session with the
existing `IDocumentCollection` methods. `session.Database` carries collection changes into the
session transaction. Index definitions are changed with OQL on the session, using its active
transaction or an automatic statement transaction; `IDocumentDatabase` has no index-management
members.

```csharp
await using var engine = DocumentDatabaseEngine.Create(new());
var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("shop");
await using var session = await database.CreateSessionAsync();
var scoped = (IDocumentDatabase)session.Database;
var orders = await scoped.CreateCollectionAsync("orders");
await orders.PutAsync(session, "order/1", "{\"customer\":{\"name\":\"Ada\"},\"total\":42}"u8.ToArray());
await session.ExecuteAsync("CREATE INDEX by_total ON orders (total)");
var result = await session.ExecuteAsync("SELECT o.customer.name FROM orders o WHERE o.total >= 40");
```

`AddDocuments((context, engine) => ...)` captures engine construction on the root
`IDatabaseApplicationBuilder` and returns that application builder. During Build,
the callback configures `IDocumentDatabaseEngineBuilder`, including an optional
borrowed `IDocumentStorageStrategy` and deferred worker/server factories. The
application owns the resulting engine and its nested components. The model has
no Hosting dependency. Standalone Create remains available; all four built-in
workers start with engine creation and stop when the engine is disposed.

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
