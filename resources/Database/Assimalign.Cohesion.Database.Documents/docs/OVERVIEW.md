# Documents engine

`Assimalign.Cohesion.Database.Documents` implements named logical databases containing
versioned UTF-8 JSON documents. Each session is bound to one database. The engine
supports document CRUD, an explicit OQL query subset, secondary B+Tree indexes,
snapshot and read-committed transactions, and durable file or in-memory storage.

The public entry point is `DocumentDatabaseEngine.Create(options)`. Cast a created
database to `IDocumentDatabase`, create a collection, and use a session with the
existing `IDocumentCollection` methods. `session.Database` carries collection and
index changes into the session transaction. A database handle used directly runs
collection/index changes in automatic transactions.

```csharp
await using var engine = DocumentDatabaseEngine.Create(new());
var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("shop");
await using var session = await database.CreateSessionAsync();
var scoped = (IDocumentDatabase)session.Database;
var orders = await scoped.CreateCollectionAsync("orders");
await orders.PutAsync(session, "order/1", "{\"customer\":{\"name\":\"Ada\"},\"total\":42}"u8.ToArray());
await scoped.CreateIndexAsync("orders", "by_total", "total");
var result = await session.ExecuteAsync("SELECT o.customer.name FROM orders o WHERE o.total >= 40");
```

`AddDocumentDatabase` is a C# extension member on the root
`IDatabaseApplicationBuilder`. It creates and registers an operational engine;
it has no Hosting dependency. The four workers start with engine creation and
stop before disposal durably closes databases.

The engine references the Documents Language, Catalog, and Storage packages and
the shared Database root. [DESIGN.md](DESIGN.md) describes transaction ownership,
query semantics, and limits. The [language design](../../Assimalign.Cohesion.Database.Documents.Language/docs/DESIGN.md)
defines the supported grammar; the [storage design](../../Assimalign.Cohesion.Database.Documents.Storage/docs/DESIGN.md)
defines the on-disk format. Wire clients, replication, security, hosting wiring,
ApplicationModel, and compiled-schema provisioning are outside this implementation.
