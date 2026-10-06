# Assimalign.Cohesion.Database.Sql — Overview

The SQL model engine of the Cohesion Data Platform: `SqlDatabaseEngine` manages
database lifecycles; sessions execute real SQL through the planner
(`Internal/SqlPlanner`) and plan executor (`Internal/SqlPlanExecutor`) against
shared storage, with DDL flowing through the relational catalog
(`Database.Sql.Catalog`) and transactions riding the storage write-ahead log.

## Scope

- **Engine lifecycle** — create/open/drop/enumerate databases over a storage
  strategy (file-backed or in-memory); each database owns a data file set and a
  dedicated catalog file set.
- **Sessions and transactions** — explicit transactions map to storage
  transactions (durable commit, page-image rollback); statements outside a
  transaction auto-commit. The session and transaction are sealed leaves of the root
  bases, which own the end state machine: a failed statement leaves the transaction
  active, and a transaction the kernel ended under its caller refuses work with
  `COHSQLT005` until it is rolled back.
- **Database scope (A5)** — every session stays bound to the database that
  created it. Qualified table references resolve only within that database's
  catalog; SQL cannot switch databases or manage the server. Conformance tests
  keep identically named tables in two databases isolated and reject attempts
  to select another database or create/drop databases through a session.
- **Compiled-schema provisioning** — `SqlDatabase.ApplySchemaAsync` diffs a validated
  `CompiledSchema`, renders deterministic table/column/index DDL into parsed
  `SqlQueryRequest`s, compensates completed reversible steps on failure, and
  records the canonical document/hash only after live-catalog convergence.
- **SQL execution** — the declared dialect (`Database.Sql.Language/docs/DIALECT.md`)
  planned rule-based and executed against table scans: `SELECT` with `WHERE`,
  projection, `ORDER BY`, `LIMIT/OFFSET`, `DISTINCT`, lone `COUNT(*)`;
  `INSERT` (multi-row, defaults, nullability); `UPDATE`/`DELETE` with accurate
  affected counts; `CREATE/ALTER/DROP TABLE`. Unsupported dialect features fail
  at plan time with precise messages. Statements nest at most the engine's
  `ExpressionNestingLimit` (256 levels by default, 32..4096; an `AND`/`OR` chain
  of any length is one level), and a statement within it that exhausts the
  executing thread's stack fails with `COHSQLE004` instead of ending the process
  (#1151). Typed requests for an engine with another limit parse with it through
  `SqlQueryRequest.FromSql(sql, parameters, parserOptions)`.
- **Typed rows** — rows encode with the shared self-describing tuple codec,
  prefixed by the owning table's object id (tables share one record space and
  scans filter by it).
- **The wire-protocol server** — `SqlDatabaseServer` (+ `SqlDatabaseServerOptions`)
  fronts one engine on a configured `Connections` listener that it binds at
  start and releases at stop; the session state
  machine, guardrails, and two-phase drain are implemented inside this package
  (servers are per-model and each model carries its own copy of the machinery —
  owner decision 2026-07-14; see DESIGN.md).
- **Storage operations** — a failed journal or data fsync takes the database offline, both
  file sets at once: every later operation, in process and over the server, is refused with
  `DatabaseOfflineException` (`COHSQLT004`) until `OpenDatabaseAsync` reopens it and
  recovery decides the unconfirmed commit (#1243); a DDL statement running at the failure is
  reported unconfirmed, and `OfflineDatabases` feeds health. `BufferPoolCapacity` (32 MiB),
  `CheckpointJournalSize` (256 MiB) and `CheckpointInterval` (5 minutes) size the pool and
  trigger checkpoints (#1254); a failed undo is retried on a 100 ms backoff (#1226). See
  DESIGN.md, "Storage operations".

## Usage

```csharp
// A data machine: operational from Create (background workers running), no
// start ceremony; dispose to durably flush and close.
await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { RootPath = dataDirectory });

var database = await engine.CreateDatabaseAsync("app");
await using var session = await database.CreateSessionAsync();

await session.ExecuteAsync(SqlQueryRequest.FromSql(
    "CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(100));"));
await session.ExecuteAsync(SqlQueryRequest.FromSql(
    "INSERT INTO users (id, name) VALUES (@id, @name);",
    new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Ada" }));
```

### Registering on a database application

`AddSql` captures engine intent through the area root's dependency-free builder
contract. The application builds and owns the engine; its optional server belongs
to that engine. The callback and nested factory execute during Build:

```csharp
builder.AddSql((context, engine) =>
{
    engine.EngineName = "orders";
    engine.RootPath = dataDirectory;
    engine.Durability = StorageCommitDurability.Grouped;
    engine.ExpressionNestingLimit = 512; // optional; 256 by default, 32..4096
    engine.AddServer(databaseEngine =>
    {
        var options = new SqlDatabaseServerOptions();
        options.Listen(new Uri("tcp://127.0.0.1:5439"));
        return SqlDatabaseServer.Create(databaseEngine, options);
    });
});
await using var application = builder.Build();
var orders = (SqlDatabaseEngine)application.Context.GetEngine("orders"); // typed lookup arrives in phase 6
```

The `Listen` helper ships in `Database.Sql.Tcp`. Omit `AddServer` for embedded
SQL. `SqlDatabaseEngine.Create(options)` also remains available for standalone use.
See [DESIGN.md](DESIGN.md) for the execution model and its decisions.
