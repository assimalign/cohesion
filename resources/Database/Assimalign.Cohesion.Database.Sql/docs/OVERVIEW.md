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
  transaction auto-commit.
- **SQL execution** — the declared dialect (`Database.Sql.Language/docs/DIALECT.md`)
  planned rule-based and executed against table scans: `SELECT` with `WHERE`,
  projection, `ORDER BY`, `LIMIT/OFFSET`, `DISTINCT`, lone `COUNT(*)`;
  `INSERT` (multi-row, defaults, nullability); `UPDATE`/`DELETE` with accurate
  affected counts; `CREATE/ALTER/DROP TABLE`. Unsupported dialect features fail
  at plan time with precise messages.
- **Typed rows** — rows encode with the shared self-describing tuple codec,
  prefixed by the owning table's object id (tables share one record space and
  scans filter by it).

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

The model's builder verbs compose against the area root's
`IDatabaseApplicationBuilder` seam (no hosting reference — the verbs ship here,
per the area builder pattern). `AddSqlDatabase` registers the engine;
`AddSqlServer` fronts it with the SQL model's wire-protocol server
(`SqlDatabaseServer`, derived from the `Database.Server` guided base):

```csharp
SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
{
    options.RootPath = dataDirectory;          // omit for in-memory
    options.Durability = StorageCommitDurability.Grouped;
});

SqlDatabaseServer server = builder.AddSqlServer(engine, options =>
{
    options.Listener = listener;               // the bound transport listener
});
```

See [DESIGN.md](DESIGN.md) for the execution model and its decisions.
