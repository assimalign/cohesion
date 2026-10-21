# Assimalign.Cohesion.Database.Sql — Overview

## Wire contract ownership

`Assimalign.Cohesion.Database.Sql.Protocol` owns this model's wire messages, codecs, family singleton and transferred value shapes (5 public types). Both engine and remote client reference that one assembly. Its public namespace and RootNamespace equal its project name. Engine requests, planning, catalog, storage and server lifecycle stay in `Assimalign.Cohesion.Database.Sql`. This extraction preserves wire bytes and validation; moved public types intentionally change namespace and assembly identity with no forwarders.


The SQL model engine of the Cohesion Data Platform: `SqlDatabaseEngine` manages
database lifecycles; sessions execute real SQL through the planner
(`Internal/Planning/SqlPlanner`) and plan executor (`Internal/Execution/SqlPlanExecutor`) against
shared storage, with DDL flowing through this assembly's relational Catalog component
(`src/Catalog/`, namespace `Database.Sql.Catalog`) and transactions riding the storage write-ahead log.

## Scope

- **Engine lifecycle** — create/open/drop/enumerate databases over a storage
  strategy (file-backed or in-memory); each database owns a data file set and a
  dedicated catalog file set.
- **Sessions and transactions** — explicit transactions map to storage
  transactions (durable commit, page-image rollback); statements outside a
  transaction auto-commit. The session and transaction are sealed leaves of the root
  bases, which own the end state machine: a failed statement leaves the transaction
  active, and a transaction the kernel ended under its caller refuses work with
  `COHSQLT005` until it is rolled back. A storage, I/O or index failure (`COHSQLE041`, or
  `COHDBS003` for corruption) is the exception: it aborts the explicit transaction, which
  then refuses work with `COHSQLT005` naming that cause until it is rolled back (#1364). In
  autocommit the next statement can run.
- **Database scope (A5)** — every session stays bound to the database that
  created it. Qualified table references resolve only within that database's
  catalog; SQL cannot switch databases or manage the server. Conformance tests
  keep identically named tables in two databases isolated and reject attempts
  to select another database or create/drop databases through a session.
- **Declared databases and provisioning** — the engine builder compiles declarations before I/O,
  then opens/creates their databases and runs data-independent steps. On populated tables, indexes,
  constraints and validation-only column tightening are persisted as pending before any cheap DDL commits and run by the engine's
  provisioning worker. Pending tables are locked before Build returns; independent tables serve
  throughout, and health reports the step and row progress. Same-table column additions retain
  declaration order across the inline/worker split. `RunMigrationsInline = true` keeps all
  steps in Build per database. Build/BuildAsync accept cancellation, including recovery.
  `ApplySchemaAsync(SqlCompiledSchema)` remains inline. Both paths use the same DDL planner and
  B2's complete catalog snapshot, typed defaults and crash convergence. Format 9 adds ordered
  pending intent to the generation-stamped applied document. An unchanged applied hash/generation
  with no pending intent still skips in constant time. Deferred failure takes only that database
  offline with `COHSQLP004`, retaining intent for the next open. Inline failures retain compensation;
  Verify drift is coded and identifies the first difference. The engine refuses to drop a declared
  database or apply a different schema than its declaration.
- **SQL execution** — the declared dialect (`Database.Sql.Language/docs/DIALECT.md`)
  planned with rules and executed over stored, system and derived relations: `SELECT` with `WHERE`,
  projection, aliased derived sources, `ORDER BY` with per-key `NULLS FIRST/LAST`, `LIMIT/OFFSET`, `DISTINCT`, grouping and aggregates;
  non-recursive `WITH` before SELECT/INSERT/UPDATE/DELETE, with ordered SELECT definitions
  materialized once per statement and optional complete output column lists;
  `INSERT` (multi-row, whole-item `DEFAULT`, `DEFAULT VALUES`, nullability);
  `UPDATE` (including `SET c = DEFAULT`)/`DELETE` with correlated and uncorrelated WHERE subqueries and accurate
  affected counts; `CREATE/ALTER/DROP TABLE`. DROP unpublishes immediately and the
  version-purge worker reclaims its data/index pages after older readers finish.
  Unsupported dialect features fail
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
  `SELECT` without `FROM` returns one row, so `SELECT f(x)` tries a function directly (#1373). A compiled schema's
  `table.Check(name, sql)` binds to the frozen catalog before the build touches any file
  (`COHSQLP001` when it does not); a schema that is new or changed also has every CHECK
  qualifier validated, while an already-applied schema keeps its recorded binding (#1191). A stored CHECK whose function a later build no longer
  registers, or registers with a result that no longer fits, does not stop its database from
  opening: its reads proceed, each write that would
  evaluate it fails with `COHSQLE009`, and an engine that declares the database fails its build
  with that code. `COHESION_SCHEMA.FUNCTIONS` lists the catalog and the special forms.
  `samples/Assimalign.Cohesion.Database.Sql.AotSample` is the NativeAOT guard for all of it, in
  process and over the wire. See DESIGN.md, "Functions (E2)".
- **Types, domains and casts** — `SqlDatabaseEngineBuilder.Types` holds the built-in types and
  the built-in casts, and an application's domains (`SqlType.CreateDomain(name, baseType,
  check?)`, added with `Types.Add`) and casts to or from them (`SqlCast.Create<TSource,
  TTarget>(...)`, added with `Types.AddCast`), frozen into `SqlDatabaseEngine.Types`. DDL and
  `CAST` resolve a built-in type name first and a domain otherwise (`COHSQLE010` for neither).
  A column of a domain stores its base type and keeps the domain's name in the catalog; every
  write that assigns it, and every `CAST` to it, runs the domain's immutable check. A database
  whose column names a domain the opening engine does not register opens, reads, and refuses
  writes to that column with `COHSQLE010`. `CAST` applies the registry's casts, the engine's own
  (now including text to DATE, TIME, TIMESTAMP, TIMESTAMPTZ, INTERVAL, UUID, REAL and DOUBLE)
  and the application's alike. A typed schema names a domain with `table.Column(selector,
  "email")`, and `schema.Type<T>(t => t.Decimal(p, s))` declares one. See DESIGN.md, "Types,
  domains and casts (E3)".
- **Fixed-length characters** — CHAR(n) has its own Character type, stores and returns padded
  strings, and ignores trailing spaces for comparison, grouping and index keys. CHAR-to-text
  casts and concatenation strip padding; LENGTH ignores it, while LIKE sees padded input.
  Domains and function parameters/results preserve their CHAR lengths. Compiled schemas use
  format v7; result-header format 2 carries Character identity and the declared length.
- **SQL definitions and declared scripts (E4)** — database-local SQL functions use the same
  bound expression evaluator and scalar ABI as registered functions. SQL domains share type
  resolution and write enforcement with registered domains. Canonical definitions, dependencies,
  ownership, script receipts and table reclamation intents persist in catalog format 25. A builder's named scripts run
  inline after its typed schema, in declaration order, and resume at committed statement
  boundaries after a crash. Checksums prohibit edits to an applied name; Verify checks receipts;
  an unchanged script-set hash and catalog generation skip reconciliation in constant time.
  See DESIGN.md, "SQL-defined functions, domains and declared scripts (E4)".
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
await using var engine = SqlDatabaseEngine.Create("app-sql", new SqlDatabaseEngineOptions { RootPath = dataDirectory });

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
SQL. `SqlDatabaseEngine.Create(name, options)` also remains available for standalone use.
See [DESIGN.md](DESIGN.md) for the execution model and its decisions.

`SqlDatabaseServerOptions.Listener` takes an `IDuplexConnectionListener`, including TCP and InMemory listeners. The server owns accepted duplex connections and their protocol I/O.

## Session execution (#1576, 2026-10-16)

`SqlDatabaseSession` derives from `DatabaseLanguageSession<SqlQueryRequest>`. It offers typed
and text execution; a foreign-model request fails compilation. `SqlQueryRequest` owns its typed
`Statement` and optional `Parameters` over the memberless `DatabaseRequest` root. Results
use the model-neutral `DatabaseResult` family. Lifecycle and explicit transactions remain
on `DatabaseSession`.

An interrupted create is adopted only with its durable sql.create claim and initial empty
data storage. Pre-header files can be restarted through CreateDatabaseAsync or declared
application Build when no other persisted state conflicts. All catalog sizes are probed read-only
before a format refusal. SQL remains at format 25; the claim does not change record layouts.
Missing sidecars and ambiguous lost catalogs are preserved and refused.


## Catalog component

The Catalog component is part of `Assimalign.Cohesion.Database.Sql`, with public types in `src/Catalog/` and helpers in `src/Internal/Catalog/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Catalog: Sql catalog overview

The relational catalog of the SQL model: SQL namespaces, tables, columns, and constraints
(`SqlCatalogTable`/`SqlCatalogColumn`), persisted through the storage kernel so
metadata gets the same page/WAL durability as data, plus persistence for the
physical index registrations exported by `Database.Indexing`.

#### Catalog: Scope

- **SQL namespaces** — immutable `SqlCatalogNamespace` descriptions persist empty namespaces
  and join statement catalog snapshots. CREATE SCHEMA and DROP SCHEMA are durable catalog
  publications; CASCADE removes contained tables/indexes and external incoming foreign keys
  atomically. Namespace ownership is separate from compiled-schema identity.

- **Fixed-length characters** — columns retain Character identity and length; domains and
  function signatures retain CHAR(n). The [current record-space format](../../Assimalign.Cohesion.Database.Sql/docs/DESIGN.md#execution-model)
  retains this identity and trimmed index keys; applied and pending compiled documents use schema format v7.

- **Metadata model** — table descriptions with stable `ObjectId`s, ordered columns
  typed by the shared type system (`DatabaseTypeInfo`), nullability, default
  literals, and primary-key constraints.
- **Transactional DDL** — create/drop table, add/drop column; each operation is a
  self-committing storage transaction (durable when the call returns).
  DROP atomically records the retired identity for the SQL worker to reclaim afterwards.
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

- **SQL definitions and scripts** — the current format retains format 10's function bodies, domains, dependencies,
  ownership, statement-prefix script receipts and a script-set reconciliation marker. Catalog
  mutations can enlist a receipt in their existing transaction; the catalog remains language-free.

#### Catalog: Dependencies

`Database` (root), `Database.Storage`, the local `src/Storage/` record facade
its records persist through, `Database.Types` (type identities + the record
codec), `Database.Indexing` (registration types). Deliberately language-free:
translating parsed DDL (`SqlCreateTableExpression`) into catalog calls is the
planner's job.

#### Catalog: Usage

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


## Storage component

The Storage component is part of `Assimalign.Cohesion.Database.Sql`, with public types in `src/Storage/` and helpers in `src/Internal/Storage/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Storage: Sql storage overview

The SQL model's storage facade: `SqlStorage` is the concrete `Storage` implementation
(model `StorageModel.Sql`) the SQL engine composes per database file set. It exposes
row-grain operations — insert/read/update/delete by `(PageId, SlotIndex)` — over the
shared page/WAL substrate from `Assimalign.Cohesion.Database.Storage`, plus the
factory methods (`Create`/`Open`) the engine's storage strategies call.

- **Consumed by:** the engine and Catalog components in `Assimalign.Cohesion.Database.Sql`:
  one data file set for rows and one catalog file set for metadata records.
- **Depends on:** `Assimalign.Cohesion.Database.Storage` only.
- **Docs:** design record in [DESIGN.md](DESIGN.md); the substrate's invariants
  (WAL ordering, recovery, buffer pool, per-owner record chains) are documented in
  the Storage project's `docs/DESIGN.md`.
