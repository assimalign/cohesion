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
- **Declared databases and provisioning** — `SqlDatabaseEngine.CreateBuilder(name)` (or the
  `AddSql(name, sql => ...)` verb) declares the databases the engine owns
  (`sql.AddDatabase("sales", database => database.Schema(...))`, `sql.AddDatabase(schema)`), each
  with its default collation and `SqlProvisioningMode` (`Apply` or `Verify`). `BuildAsync` (and
  `Build`, which bridges it) compiles every declaration before any file is touched, creates and
  composes the engine, then opens or creates and provisions each declared database before it
  returns; a failure disposes the engine, and a canceled build throws `OperationCanceledException`.
  The engine refuses to drop a declared database. `SqlDatabase.ApplySchemaAsync(SqlCompiledSchema)`
  applies a schema imperatively, except another schema than its declaration's to a declared
  database. Both diff the validated `SqlCompiledSchema`, render deterministic table/column/index DDL
  into parsed `SqlQueryRequest`s, compensate completed reversible steps on failure, and record the
  canonical document/hash only after live-catalog convergence. Failures lead with `COHSQLP001` to
  `005`, and a `Verify` drift names the first object that differs. A server keeps a copy of its
  options, as the engine does.
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
- **Functions** — `SqlDatabaseEngineBuilder.Functions` holds the standard library (`UPPER`,
  `LOWER`, `LENGTH`, `ABS`, `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`) and an application's own,
  registered the same way: a `SqlScalarFunction` or `SqlAggregateFunction` leaf, or the typed
  shorthands `SqlScalarFunction.Create<T1, TResult>(...)` (up to four arguments) and
  `SqlAggregateFunction.Create<TState, T1, TResult>(...)`, over the allocation-free value ABI
  (`SqlValue`, `SqlArguments`, `SqlFunctionContext`). The build freezes them into
  `SqlDatabaseEngine.Functions`, visible in every database of the engine. A built-in cannot be
  replaced or shadowed (an overload of its name must take another number of arguments), and a
  reserved, type or quantifier name cannot be registered. Calls resolve by name,
  argument count and type once per statement (an ambiguous call is `COHSQLE008`); an `Immutable`
  call over constants is folded; a strict function (the default; a typed one can be created
  `CalledOnNullInput`) is not called over NULL; a CHECK admits only `Immutable` functions; and
  what a function throws, or a result of another type than it declares, fails the statement as
  `COHSQLE007`. One function instance serves every session, so it must be thread-safe. A
  `SELECT` needs a `FROM` in this dialect, so try a function as `SELECT f(x) FROM t`. A compiled schema's
  `table.Check(name, sql)` binds to the frozen catalog before the build touches any file
  (`COHSQLP001` when it does not). A stored CHECK whose function a later build no longer
  registers, or registers with a result that no longer fits, does not stop its database from
  opening: its reads proceed, each write that would
  evaluate it fails with `COHSQLE009`, and an engine that declares the database fails its build
  with that code. `COHESION_SCHEMA.FUNCTIONS` lists the catalog and the special forms.
  `samples/Assimalign.Cohesion.Database.Sql.AotSample` is the NativeAOT guard for all of it, in
  process and over the wire. See DESIGN.md, "Functions (E2)".
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
  recovery decides the unconfirmed commit (#1243); a DDL statement that had committed part of
  itself at the failure is reported unconfirmed and one that had committed nothing is refused
  (#1272), and `OfflineDatabases` feeds health. `BufferPoolCapacity` (32 MiB),
  `CheckpointJournalSize` (256 MiB) and `CheckpointInterval` (5 minutes) size the pool and
  trigger checkpoints (#1254); a failed undo is retried on a 100 ms backoff (#1226). See
  DESIGN.md, "Storage operations".
- **Closing** — a database its holder disposed is forgotten once the close ends, and
  `OpenDatabaseAsync` opens it again with its rows, in memory as on disk (owner decision 33).

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
contract, under the engine name it reserves at the call. The application builds and owns
the engine; its declared databases are provisioned while it is built, and its optional
server belongs to it. The callback and nested factories execute during Build:

```csharp
builder.AddSql("orders", sql =>
{
    sql.Options.RootPath = dataDirectory;
    sql.Options.Durability = StorageCommitDurability.Grouped;
    sql.Options.ExpressionNestingLimit = 512; // optional; 256 by default, 32..4096
    sql.AddDatabase("sales", database =>
    {
        database.DefaultCollation = Collation.CaseInsensitive;   // Binary when unset
        database.Schema(schema => schema.Table<Order>("orders", table => table.Key(order => order.Id)));
    });
    sql.AddServer(server => server.Listen(new Uri("tcp://127.0.0.1:5439")));
});
await using var application = builder.Build();
SqlDatabaseEngine orders = application.Context.GetEngine<SqlDatabaseEngine>("orders");
```

Without a host, the same builder provisions while it builds:

```csharp
SqlDatabaseEngineBuilder sql = SqlDatabaseEngine.CreateBuilder("local");
sql.Options.RootPath = dataDirectory;
sql.Functions.Add(SqlScalarFunction.Create("slugify",
    static (string text) => text.ToLowerInvariant().Replace(' ', '-'), SqlFunctionVolatility.Immutable));
sql.AddDatabase(SalesSchema.Declaration);                        // a reusable SqlSchema value whose
                                                                 // table.Check("ck_sku", "slugify(Sku) <> ''")
                                                                 // binds to slugify before any file
await using SqlDatabaseEngine engine = await sql.BuildAsync(cancellationToken);
SqlDatabase sales = await engine.OpenDatabaseAsync("sales", cancellationToken);
```

The `Listen` helper ships in `Database.Sql.Tcp`. Omit `AddServer` for embedded
SQL. `SqlDatabaseEngine.Create(options)` also remains available for standalone use.
See [DESIGN.md](DESIGN.md) for the execution model and its decisions.
