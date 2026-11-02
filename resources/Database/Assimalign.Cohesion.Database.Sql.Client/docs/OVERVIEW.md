# Assimalign.Cohesion.Database.Sql.Client

SqlConnection.CopyToAsync streams negotiated text/CSV table exports to a caller-owned Stream with bounded buffering and a verified data-row count (#1724).

The typed SQL client: a relational surface — commands, typed result sets, a
SQL-scoped error taxonomy, and a telemetry hook — layered over the shared
`Assimalign.Cohesion.Database.Client` core.

## Purpose

`Database.Client` is the model-agnostic client core: it dials the server, runs the
wire handshake, and pools authenticated connections. This package owns SQL result
materialization and the SQL-shaped ergonomics an application expects — an
ADO.NET-familiar command/parameter/result-set surface with typed column access —
without re-implementing any of the transport, framing, or pooling below it.

The client **does not parse SQL**. It sends statement text and parameters over the
wire; the server's SQL session parses, plans, and executes them. That keeps the
client contract stable and independent of the engine's internal plan structures.
Its wire codecs come from `Database.Sql.Protocol`; it references neither the SQL engine
nor `Sql.Language`.

## Scope

- `SqlClient.Create(SqlClientOptions)` → the sealed `SqlClient`: a pooling client bound to one
  database on one server.
- `SqlClient.ConnectAsync()` → the sealed `SqlConnection`: rents a typed connection (a pooled,
  authenticated session under the hood).
- `SqlCommand` + `SqlParameterCollection`: statement text with named parameters
  (the `@`/`$` sigil is normalized away on bind).
- `SqlConnection.QueryAsync` / `ExecuteAsync` / `ExecuteScalarAsync<T>`: run commands
  and get a `SqlResultSet` with rows and affected count, a count alone, or a scalar.
- `SqlResultSet` / `SqlRow` / `SqlColumn`: typed, ordinal- and name-addressable rows
  with widening numeric getters.
- `SqlClientException` + `SqlClientErrorKind`: a SQL-scoped failure taxonomy mapped
  from the core's wire codes, with a `ConnectionUsable` flag.
  ExecutionFailure can end a partial result: QueryAsync throws and discards all partial
  rows while keeping the connection usable. Writes prepare headers and rows before commit;
  encoding failures roll back only the statement. Reads encode during delivery, and late failures
  preserve the explicit transaction and connection. Both paths enforce per-frame limits with no
  aggregate result cap. Transport failures during delivery are terminal (#1651).
- `SqlClientObserver`: an abstract, allocation-free telemetry hook fired around every
  command; an observer overrides only the hooks it needs.

For a constraint failure, catch `SqlClientException` and read
`exception.Diagnostics[0].Code`: CS03037 (unique), CS03038 (CHECK/domain),
CS03039 (foreign key), or CS03024 (NOT NULL). `exception.Code` is the protocol
category; the engine code also prefixes the message when diagnostics are not negotiated.

## Dependencies

- `Assimalign.Cohesion.Database.Client` — the pooling client core this layers over; its
  `shared/` diagnostics snapshot is also compiled in (`CohesionSharedSource`).
- `Assimalign.Cohesion.Database.Protocol` — framing, `ProtocolErrorCode` and the shared
  error/lifecycle messages.
- `Assimalign.Cohesion.Database.Sql.Protocol` — the SQL family identifiers and payload codecs.
- `Assimalign.Cohesion.Database.Types` — `DatabaseType` column identities and `Diagnostic`.
- `Assimalign.Cohesion.Connections` — the transport `IDuplexConnectionFactory` handed to
  the client core.

## Usage

```csharp
await using SqlClient client = SqlClient.Create(new SqlClientOptions
{
    Settings = DatabaseConnectionSettings.Parse("Database=orders;Endpoint=db-host:5740"),
    ConnectionFactory = tcpConnectionFactory,   // composed statically, never in the string
});

await using SqlConnection connection = await client.ConnectAsync();

SqlResultSet rows = await connection.QueryAsync(
    new SqlCommand("SELECT id, name FROM users WHERE id = @id").WithParameter("id", 42));

foreach (SqlRow row in rows)
{
    int id = row.GetInt32("id");
    string name = row.GetString("name");
}

long affected = await connection.ExecuteAsync("DELETE FROM users WHERE id = @id",
    new Dictionary<string, object?> { ["id"] = 42 });
```

RETURNING writes use QueryAsync to retrieve typed rows and SqlResultSet.AffectedCount
in one exchange (-1 for SELECT, n for DML). ExecuteAsync returns only n and discards
returned rows; ExecuteScalarAsync retrieves the first cell, including a generated identity.

See `docs/DESIGN.md` for the design decisions behind the surface.

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

Pool return now rolls back unfinished SQL/Graph transactions using negotiated server status before reusing a session; uncertified sessions are discarded (#1540).

## Sql diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Rules audit after main cleanup

Private test constants use repository casing. The transaction-outcome guidance is self-contained:
connection loss after COMMIT can remain uncertain, and the client supplies no automatic replay
or reconciliation operation. The removed mapper package is no longer a documentation dependency.
Wire bytes, diagnostics, pooling and public APIs are unchanged.

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

Connection inspection reads its existing database name without checking connection health,
taking the shared exchange lock, dialing, or returning a lease. Result views read already
materialized columns/diagnostics and row counts; row and parameter views do not enumerate values
or call typed conversions. Client exchange behavior and wire framing remain unchanged.
