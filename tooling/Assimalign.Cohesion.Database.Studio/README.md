# Cohesion Database Studio (throw-away)

A MAUI desktop editor for exercising the Cohesion Database engines **as a consumer** while the query
languages grow: type SQL / OQL / GQL, browse catalogs, and work the key-value and blob stores. It is
not a product: no docs, no tests, not in any solution, CI workflow, or release inventory, and
`IsPackable=false`. Windows only (`net10.0-windows10.0.19041.0`, unpackaged).

## Run

```powershell
# once per fresh worktree: the repo build tasks
dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj
dotnet build tooling/Assimalign.Cohesion.Database.Studio/Assimalign.Cohesion.Database.Studio.csproj
dotnet run --project tooling/Assimalign.Cohesion.Database.Studio   # or double-click the exe under bin/Debug/.../win-<arch>/
```

Headless smoke run (engines in a temp folder unless you pass one; a `smoke` database is dropped and
recreated in each model):

```powershell
& .\Assimalign.Cohesion.Database.Studio.exe --smoke [dataRoot] | Out-Host   # piping makes PowerShell wait for the WinExe
```

It prints one `PASS`/`FAIL`/`SKIP` line per step, mirrors them to `studio-smoke.log` next to the
exe, and exits non-zero when a step fails. It covers, per model, create/insert/query embedded and
over loopback TCP, transactions, the catalog, and every Samples script. UI crashes are logged to
`studio-crash.log` next to the exe; the UI marks them handled and keeps running.

## Modes (Workspace page)

One engine per model runs in-process under `<data root>\<model>` (default
`%LOCALAPPDATA%\CohesionStudio\data`). **Apply** disposes everything and rebuilds.

| Mode | What the page talks to |
| --- | --- |
| Embedded | The engine through `IDatabaseSession` (explicit Begin/Commit/Rollback with an isolation level). |
| Wire (loopback) | The Studio starts that model's server on `127.0.0.1:<port>` (0 = OS-assigned) over the same engine and uses the real client (`Sql/Graph/KeyValuePair/Blob.Client`). Database list/create/drop, KEYSPACES and blob containers still go through the engine: the wire has no management verbs. |
| Wire (external) | The real client against an already-running server (for example the SampleHost fixture). Type the database name; nothing can be listed or created. |

Documents is **embedded only**: there is no Documents server, and `Documents.Client/src` is empty.

## Pages

- **SQL / Documents (OQL) / Graph (GQL)**: editor (F5 or Ctrl+Enter runs the selection, or
  everything), statement-by-statement execution (the script is cut at each `;` the engines' shared
  `TokenLexer` reads, so comments and quotes end where the engines end them, and the text is sent as
  typed, CR line breaks included), result grid (tap a row for full values, Copy TSV),
  Messages with status, affected count, elapsed ms and every diagnostic (click one to select it in the
  editor), catalog explorer (click a highlighted entry to insert a query), session history, Samples.
  Diagnostics come from the engine and from a local parse with the language package, because
  `ExecuteAsync(string)` throws on the first parse error. SQL over the wire sends BEGIN/COMMIT/ROLLBACK
  for the transaction buttons. Graph **Auto** mode sends `MATCH ... RETURN <one variable>` through
  `ExecutePaths` and renders `(a:Label {..})-[:TYPE]->(b ...)`.
- **Documents** also has collection tools (create/drop collection, put/get/delete a document, seed
  sample data), because OQL has no data-mutation syntax.
- **Key-Value**: get / put (unconditional, if absent, if ETag matches) / delete / exists, prefix or
  range scans with a limit, KEYSPACES. Keys and values as UTF-8 text or hex.
- **Blob**: containers, blob listing with a prefix, upload (file picker), download to a folder,
  delete, properties, container ownership.

## Known gaps (Studio)

- Wire (external) cannot list/create/drop databases, run KEYSPACES, or manage blob containers.
- No statement parameters; no cancellation of a statement the engine does not observe.
- Graph index creation is only `GraphSchema.CreateIndexAsync`, not exposed here (GQL has no index DDL).
- Settings are not persisted between runs.

## Engine/client gaps found while building it

- `session.ExecuteAsync(string)` throws `DatabaseParseException` carrying only the first error for SQL,
  OQL and GQL (`FromSql`/`FromOql`/`FromGql` throw); a `SqlQueryRequest` built from the parser's
  statement instead returns every diagnostic in `QueryResult.Diagnostics`.
- `ISqlConnection.QueryAsync` drops the affected count and `ExecuteAsync` drops rows, so a consumer
  has to know the statement kind in advance. The wire client reports no diagnostics, only
  `SqlClientException` kind/code/message. `IGraphConnection.QueryAsync` returns both.
- A missing database over the wire is reported nondeterministically: the servers write
  `DatabaseNotFound` and then close, and the client sometimes surfaces that code and sometimes
  `Internal: The server closed the connection mid-exchange`. Seen for SQL, Graph and Key-Value
  across runs (smoke step `unknown-database (observation)` prints what each client said).
- SQL and Key-Value `GetDatabasesAsync` list only databases opened in this process; Documents, Graph
  and Blob enumerate storage. The Studio's "Discover on disk" opens each folder to compensate.
- `KeyValueScanRange` / `KeyValueScanOptions` use `ReadOnlyMemory<byte>?`: assigning a null `byte[]`
  yields an empty but *present* bound, which then trips "A prefix scan cannot combine with explicit
  start/end bounds".
- No KEYSPACES verb on `IKeyValueConnection`; no container verbs on `IBlobConnection`.
- OQL `COUNT(*)` is typed `Decimal`, while SQL `COUNT(*)` is `Int64`.
