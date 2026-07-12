# Assimalign.Cohesion.Database.Sql.Client

The typed SQL client: a relational surface — commands, typed result sets, a
SQL-scoped error taxonomy, and a telemetry hook — layered over the shared
`Assimalign.Cohesion.Database.Client` core.

## Purpose

`Database.Client` is the model-agnostic client core: it dials the server, runs the
wire handshake, pools authenticated connections, and materializes rows as boxed
values. This package adds the SQL-shaped ergonomics an application expects — an
ADO.NET-familiar command/parameter/result-set surface with typed column access —
without re-implementing any of the transport, framing, or pooling below it.

The client **does not parse SQL**. It sends statement text and parameters over the
wire; the server's SQL session parses, plans, and executes them. That keeps the
client contract stable and independent of the engine's internal plan structures, and
is why this package does **not** reference `Sql.Language`.

## Scope

- `SqlClient.Create(SqlClientOptions)` → `ISqlClient`: a pooling client bound to one
  database on one server.
- `ISqlClient.ConnectAsync()` → `ISqlConnection`: rents a typed connection (a pooled,
  authenticated session under the hood).
- `SqlCommand` + `SqlParameterCollection`: statement text with named parameters
  (the `@`/`$` sigil is normalized away on bind).
- `ISqlConnection.QueryAsync` / `ExecuteAsync` / `ExecuteScalarAsync<T>`: run commands
  and get a `SqlResultSet`, an affected count, or a scalar.
- `SqlResultSet` / `SqlRow` / `SqlColumn`: typed, ordinal- and name-addressable rows
  with widening numeric getters.
- `SqlClientException` + `SqlClientErrorKind`: a SQL-scoped failure taxonomy mapped
  from the core's wire codes, with a `ConnectionUsable` flag.
- `ISqlClientObserver`: an allocation-free telemetry hook fired around every command.

## Dependencies

- `Assimalign.Cohesion.Database` — the `DatabaseException` area root.
- `Assimalign.Cohesion.Database.Client` — the pooling client core this layers over.
- `Assimalign.Cohesion.Database.Types` — `DatabaseType` column identities.
- `Assimalign.Cohesion.Connections` — the transport `IConnectionFactory` handed to
  the client core.

## Usage

```csharp
await using ISqlClient client = SqlClient.Create(new SqlClientOptions
{
    Settings = DatabaseConnectionSettings.Parse("Database=orders;Endpoint=db-host:5740"),
    ConnectionFactory = tcpConnectionFactory,   // composed statically, never in the string
});

await using ISqlConnection connection = await client.ConnectAsync();

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

See `docs/DESIGN.md` for the design decisions behind the surface.
