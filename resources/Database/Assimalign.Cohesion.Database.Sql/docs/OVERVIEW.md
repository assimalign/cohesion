# Assimalign.Cohesion.Database.Sql — Overview

TRUNCATE table lists execute through atomic storage replacement, with recursive FK closure,
identity restart/continuation and deferred reclamation that preserves older snapshots (#1716).
CTAS remains deferred to SQ-T2.

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
  `CS04005` until it is rolled back. A storage, I/O or index failure (`CS06041`, or
  `CK06003` for corruption) is the exception: it aborts the explicit transaction, which
  then refuses work with `CS04005` naming that cause until it is rolled back (#1364). In
  autocommit the next statement can run. Writing statements prepare wire headers/rows before
  commit; encoding failures roll back only the statement. Read-only statements encode during
  delivery and can end partial results with a coded statement error. Both paths preserve the
  connection and explicit transaction on result failures; uncoded failures use CS05045.
  Each header/row is bounded by the 16 MiB frame limit, with no aggregate result cap.
  Transport failures during delivery are terminal (#1651).
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
  offline with `CS03104`, retaining intent for the next open. Inline failures retain compensation;
  Verify drift is coded and identifies the first difference. The engine refuses to drop a declared
  database or apply a different schema than its declaration.
- **Bounded DDL workspace** — validation streams rows; uniqueness compares adjacent live keys
  in a bounded cursor. The #1444 guard attributes surviving row/key workspace and charges the
  full pool, so unrelated runtime retention cannot decide its bound (DESIGN, bounded build validation).
- **SQL execution** — the declared dialect (`Database.Sql.Language/docs/DIALECT.md`)
  planned with rules and executed over stored, system and derived relations: `SELECT` with `WHERE`,
  projection, INNER/LEFT/RIGHT/CROSS joins with parenthesized groups and RIGHT-to-LEFT normalization, aliased derived sources, `ORDER BY` with per-key `NULLS FIRST/LAST`, `LIMIT/OFFSET`, `DISTINCT`, grouping and aggregates;
  non-recursive `WITH` before SELECT/INSERT/UPDATE/DELETE, with ordered SELECT definitions
  materialized once per statement and optional complete output column lists;
  `INSERT` (multi-row, whole-item `DEFAULT`, `DEFAULT VALUES`, nullability);
  `UPDATE` (including `SET c = DEFAULT`)/`DELETE` with correlated and uncorrelated WHERE subqueries and accurate
  affected counts; UPDATE FROM and DELETE USING execute collected source associations, with
  target seeks and budgeted source hash/index joins exposed through EXPLAIN; `CREATE/ALTER/DROP TABLE`. DROP unpublishes immediately and the
  version-purge worker reclaims its data/index pages after older readers finish.
  Unsupported dialect features fail
  at plan time with precise messages. Statements nest at most the engine's
  `ExpressionNestingLimit` (256 levels by default, 32..4096; an `AND`/`OR` chain
  of any length is one level), and a statement within it that exhausts the
  executing thread's stack fails with `CS05004` instead of ending the process
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
  argument count and type once per statement (an ambiguous call is `CS01008`); an `Immutable`
  call over constants is folded; a strict function (the default; a typed one can be created
  `CalledOnNullInput`) is not called over NULL; a CHECK admits only `Immutable` functions; and
  what a function throws, or a result of another type than it declares, fails the statement as
  `CS05007`. One function instance serves every session, so it must be thread-safe. A
  `SELECT` without `FROM` returns one row, so `SELECT f(x)` tries a function directly (#1373). A compiled schema's
  `table.Check(name, sql)` binds to the frozen catalog before the build touches any file
  (`CS03101` when it does not); a schema that is new or changed also has every CHECK
  qualifier validated, while an already-applied schema keeps its recorded binding (#1191). A stored CHECK whose function a later build no longer
  registers, or registers with a result that no longer fits, does not stop its database from
  opening: its reads proceed, each write that would
  evaluate it fails with `CS01009`, and an engine that declares the database fails its build
  with that code. `COHESION_SCHEMA.FUNCTIONS` lists the catalog and the special forms.
  `samples/Assimalign.Cohesion.Database.Sql.AotSample` is the NativeAOT guard for all of it, in
  process and over the wire. See DESIGN.md, "Functions (E2)".
- **Types, domains and casts** — `SqlDatabaseEngineBuilder.Types` holds the built-in types and
  the built-in casts, and an application's domains (`SqlType.CreateDomain(name, baseType,
  check?)`, added with `Types.Add`) and casts to or from them (`SqlCast.Create<TSource,
  TTarget>(...)`, added with `Types.AddCast`), frozen into `SqlDatabaseEngine.Types`. DDL and
  `CAST` resolve a built-in type name first and a domain otherwise (`CS02010` for neither).
  A column of a domain stores its base type and keeps the domain's name in the catalog; every
  write that assigns it, and every `CAST` to it, runs the domain's immutable check. A database
  whose column names a domain the opening engine does not register opens, reads, and refuses
  writes to that column with `CS02010`. `CAST` applies the registry's casts, the engine's own
  (now including text to DATE, TIME, TIMESTAMP, TIMESTAMPTZ, INTERVAL, UUID, REAL and DOUBLE)
  and the application's alike. A typed schema names a domain with `table.Column(selector,
  "email")`, and `schema.Type<T>(t => t.Decimal(p, s))` declares one. See DESIGN.md, "Types,
  domains and casts (E3)".
- **Fixed-length characters** — CHAR(n) has its own Character type, stores and returns padded
  strings, and ignores trailing spaces for comparison, grouping and index keys. CHAR-to-text
  casts and concatenation strip padding; LENGTH ignores it, while LIKE sees padded input.
  Domains and function parameters/results preserve their CHAR lengths. Compiled schemas use
  format v8; result-header format 2 carries Character identity and the declared length.
- **SQL definitions and declared scripts (E4)** — database-local SQL functions use the same
  bound expression evaluator and scalar ABI as registered functions. SQL domains share type
  resolution and write enforcement with registered domains. Canonical definitions, dependencies,
  ownership, script receipts and table reclamation intents persist in catalog format 26. A builder's named scripts run
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
  `DatabaseOfflineException` (`CS04004`) until `OpenDatabaseAsync` reopens it and
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

The `Listen` helper ships in `Database.Tcp`. Omit `AddServer` for embedded
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
before a format refusal. SQL remains at format 26; the claim does not change record layouts.
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
  retains this identity and trimmed index keys; applied and pending compiled documents use schema format v8.

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

Offline refusals and unconfirmed commits expose their existing model code through Diagnostics,
with unprefixed exception detail and the original cause. Failure tracing reads that carrier;
legacy wire error text keeps its existing code prefix. See DESIGN, structured failure codes (#1350).

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

Exact-pass purge-refusal tests own an unpumped worker independently of the engine's live
workers. Their original counts stay strict; production pump behavior is covered separately.
This removes the extra-callback race found during #1365 without changing engine scheduling.

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

## Cascading DELETE preparation (#1420)

A cascade and its SET NULL/DEFAULT actions try all pre-apply locks. Contention releases new
attempt grants, waits normally, retains the waited grant and rebuilds the entire unapplied
closure. Prior transaction grants/upgrades survive. An unapplied failure releases all new
statement grants; offline and started-apply ownership remain protected. Fixed snapshots keep
first-updater-wins checks. The shared manager repairs avoidable queue cycles before choosing
a hard-deadlock victim. Public APIs and wire messages are unchanged.

## Probe costing and hash joins (#1522)

Access selection compares bounded snapshot-visible index probes without stored statistics.
Broad safe equijoins can build an in-memory hash table; unique and small indexed probes retain
nested loops. LEFT semantics, duplicate matches, SQL equality and full residual evaluation remain.
The positive StatementMemoryBudget engine option defaults to 64 MiB and limits concurrent live
hash builds per statement; CS05067 (SQLSTATE 53200) refuses excess memory, without spill.
Plain EXPLAIN reports estimates without executing its data statement. SELECT-only EXPLAIN ANALYZE
executes to report candidate work in the same eight-column description; unavailable counts remain
NULL. Correlated child work accumulates on its own nodes and is excluded from the parent's count;
statement-wide totals include all invocations. No standalone ANALYZE, persisted statistics,
format change or new wire message is added.

Pool return now rolls back unfinished SQL/Graph transactions using negotiated server status before reusing a session; uncertified sessions are discarded (#1540).

All five server-options leaves inherit DatabaseServerOptions. Database.Tcp provides generic
Listen(Uri), retaining the concrete options type, and is public in App.Database (#1391, D7 B).
The SQL-specific TCP integration is removed; import Assimalign.Cohesion.Database for the helper.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.
Shared Engine source uses the consuming assembly's generated accessor through the internal `EngineErrorMessages` binding; no public resource accessor or cross-assembly internal access is introduced.

## Sql diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.


## Fixed-pass refusal fixture ownership (found during #1470)

Explicit-pass SQL refusal fixtures pause the engine pump at its existing trigger observer before
creating their databases. The observer acknowledges the pause before the fixture proceeds;
only then do manual iterations dispatch and collect real lanes. The worker remains attached to
the engine, preserving observational Faulted state, recovery and offline-policy coverage. In
particular, the three refused-undo episodes assert Faulted while a refusal is held and Running
after recovery. Using a detached replacement would miss that owner-state contract.

The real-pump owed-pass tests retain their normal pump. Five-millisecond delays only yield while
establishing the pause; thirty-second cancellation/join limits are hang guards. No duration or
extra retry decides an assertion, and no parallelism cap is added. This corrects a fixture race:
the engine pump could add a pass to an exact-count manually driven refusal episode. Product
worker scheduling, fault semantics, public APIs, diagnostics and storage/wire formats are unchanged.


## Shared defaults and diagnostic resources (#1470)

The Defaults shared module resolves incompatible-value text through a consuming-assembly-local
`SqlDefaultLiteralErrorMessages` binding to the generated internal resource accessor. SQL schema
and SDK extraction therefore retain identical ArgumentException text and the `value` parameter,
without a resource lookup on successful rendering. SQL explanatory message fragments also use
resources. SDK compiler identifiers remain `COHDBSDK`; embedded runtime identities use `CK00001`
for language syntax and `CS03101` for unsupported principal declarations. Schema format remains
`cohesion/database-schema/v8`; SQLSTATE, wire, diagnostics carriers and public APIs are unchanged.


The shared Types module also binds modifier-refusal templates to each consumer's own generated
resources through internal `SqlTypeModifierErrorMessages`. Unsupported type/modifier text and
codes are preserved. Numeric modifier spelling remains invariant SQL text; message templates
format with the current culture. Successful type validation adds no resource lookup.


## Shared validation message completion (#1470)

Engine option requirements and portable database-name refusal reasons, including their rule
text, resolve through each consuming model's generated internal resources. Valid name checks
still avoid resource lookup; the engine-description template is cached for construction.
Existing message text, parameter names, refused values and pre-I/O validation order remain
unchanged. No public API, error identity, SQLSTATE, storage or wire format, event or counter changes.
Local predicate guards remain with the policies they validate; reusable unconditional catalog
throws use exception-type extension members. Private cold comparison and location-attachment
paths retain their allocation and exception-identity behavior.

## Rules audit after main cleanup

The transient cost, cascade-retry and EXPLAIN analysis carriers use explicit constructors with
the same property/field layout. Private names follow repository casing; the four constructors
add no state or hot-path work. SELECT-only EXPLAIN ANALYZE, plain EXPLAIN admission, transient
probes, hash-budget refusal and cascade release/retry retain their existing contracts.
Assembly-local Engine/Types resource bindings live under Internal and keep the linked module's
owning namespace. SqlStorageTests keeps its existing cases and assertions through Shouldly.

## Error message producers and catalog carriers

Failure-detail producers now use the same generated resources as their outer exception templates:
unresolved function/domain reasons, signature arity, declaration conflicts, schema drift and
compensation explanations, and catalog size-refusal context. Persisted-definition explanation
strings are cached. Canonical SQL, type/identity spellings and protocol/diagnostic framing remain
data. Existing message text, codes and source-span policy are preserved for these resource moves.
Handshake refusal prose uses these resources too, preserving its numeric protocol categories and
current-culture formatting.

Owned catalog producers no longer depend on the public message-only SqlCatalogException
constructor. Malformed persisted catalog metadata reports CS06059 (SQLSTATE XX001); unsupported
catalog metadata formats/versions report CS06060 (0A000). Table/index declaration, stale identity,
identity range/type and script-state refusals use their existing specific identities. A truncated
typed decode receives CS06059 at the catalog boundary while retaining the same SqlCatalogException,
StorageCorruptionException, original cause, page and slot. Existing authored carriers remain intact.
The public legacy constructor, refusal behavior, storage versions and lack of upgrade readers are
unchanged. Runtime regressions pin the new carriers alongside existing message/location checks.

## Shared lifecycle diagnostic completion

Duplicate logical database/storage creation carries CK03001, whether the database is open or
represented only by its closed files. Ordinary logical absence remains the precise
DatabaseNotFoundException and carries CK03002. Each diagnostic has Error severity, the unchanged
unprefixed exception message and no statement span. Existing coded interrupted-creation failures
and original causes are retained. No ownership, file mutation, lifecycle ordering, format or
protocol message-kind change accompanies these diagnostics.

An orphan directory without its data file refuses creation with CK06004 and the unchanged remedy.
A directly opened missing physical data/catalog set carries CK06005. In-memory and file-backed
strategies share the same condition identities. The orphan-directory format text is resourced
through this assembly's existing Engine binding; no new public helper or reference is required.

Identical SQL error text shares one canonical resource entry; internal accessor aliases are deduplicated without changing the text or culture.

## Debugger inspection

Concrete source types with explicit nonpublic instance state use a private nested DebugView
and debugger display/proxy attributes. The view is the last member in the same type file and
exposes only reviewed public/protected state through read-only properties. Exception and
EventSource types remain excluded. A type with no state to filter keeps its normal member view;
a type whose base supplies both debugger attributes inherits that view.

Inspection does not perform I/O, take lifecycle locks, mutate state, invoke application callbacks,
or materialize lazy results. The additions are debugger metadata and nested helper types; existing
instance fields, runtime operations, error identities, data formats and protocol contracts do
not change. SDK and Studio source are outside this rule's scope.

The function argument view snapshots only Count; the function context view snapshots only
TransactionTimestamp. Neither view retains a ref struct, span, context, or cancellation token.
The SQL value view reads Type and IsNull without decoding its payload. Internal-only plan or
metrics state stays hidden rather than being promoted to a public debugger projection.
