# Assimalign.Cohesion.Database.Sql.Catalog — Overview

The relational catalog of the SQL model: schemas, tables, columns, and constraints
(`SqlCatalogTable`/`SqlCatalogColumn`), persisted through the storage kernel so
metadata gets the same page/WAL durability as data, plus persistence for the
physical index registrations exported by `Database.Indexing`.

## Scope

- **Metadata model** — table descriptions with stable `ObjectId`s, ordered columns
  typed by the shared type system (`DatabaseTypeInfo`), nullability, default
  literals, and primary-key constraints.
- **Transactional DDL** — create/drop table, add/drop column; each operation is a
  self-committing storage transaction (durable when the call returns).
- **Physical column layout** — each table records which physical ordinals its
  dropped columns kept (`DroppedColumnOrdinals`, `PhysicalColumnCount`,
  `GetPhysicalOrdinal`): drop marks a column's ordinal dropped and never renumbers
  the others, and add appends a new ordinal, so stored rows are never rewritten
  (#1241).
- **Atomic index publication** — `CreateIndexAsync` and `PublishTableAsync` require
  each new index description and its matching physical registration together.
  `GetIndexRegistrations` returns the committed directory for re-attachment on reopen;
  checkpoint and shutdown cannot replace it with an independently captured snapshot.
- **Applied-schema state** — `SchemaState` / `SaveSchemaStateAsync` atomically
  retain the canonical `SqlCompiledSchema` document, deterministic hash and DDL
  generation used for constant-time already-applied checks. Every committed catalog
  change advances `DdlGeneration` in its own transaction, so interrupted provisioning
  and later DDL trigger live-catalog reconciliation.

## Dependencies

`Database` (root), `Database.Storage`, `Database.Sql.Storage` (the record facade
its records persist through), `Database.Types` (type identities + the record
codec), `Database.Indexing` (registration types). Deliberately language-free:
translating parsed DDL (`SqlCreateTableExpression`) into catalog calls is the
planner's job.

## Usage

```csharp
var catalogStorage = SqlStorage.Create(dataStream, journalStream, backupStream, "mydb/catalog");
var catalog = SqlCatalog.Open(catalogStorage);

var users = await catalog.CreateTableAsync("dbo", "users", new[]
{
    new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int64), isNullable: false),
    new SqlCatalogColumn("name", new DatabaseTypeInfo(DatabaseType.String, maxLength: 100)),
}, primaryKeyColumns: new[] { "id" });
```

See [DESIGN.md](DESIGN.md) for the persistence model and decisions.
