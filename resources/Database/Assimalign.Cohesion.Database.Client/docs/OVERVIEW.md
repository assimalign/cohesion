# Assimalign.Cohesion.Database.Client — Overview

The shared client core of the Data Platform: the protocol client every per-model
client (`Sql.Client`, `Documents.Client`, …) builds on. It dials a
`libraries/Connections` transport, runs the startup/authenticate/ready
handshake, executes statement text with parameters, materializes streamed
results, and pools authenticated connections.

## Scope

- **`IDatabaseClient`** — the pooling entry point (`DatabaseClient.Create`):
  `RentAsync` returns an open, authenticated connection; disposing a rented
  connection returns it to the pool with its server session intact.
- **`IDatabaseConnection`** — one protocol session: `OpenAsync` (handshake) and
  `ExecuteAsync(statement, parameters)` returning a materialized
  `DatabaseClientResult` (typed columns + boxed rows, or an affected count).
- **`DatabaseConnectionSettings`** — typed settings with a minimal `key=value;`
  connection-string parser (`Database`, `Principal`, `Endpoint=host[:port]`,
  `MaxPoolSize`).
- **`DatabaseClientException`** — the client error root, carrying the wire's
  stable `ProtocolErrorCode`.

## Dependencies

`Database` (root contracts + exception root), `Database.Protocol` (framing +
payload schemas), `Database.Types` (the shared value codec for parameters and
rows), `Connections` (transport factories).

## Usage

```csharp
var client = DatabaseClient.Create(new DatabaseClientOptions
{
    Settings = DatabaseConnectionSettings.Parse("Database=app;Principal=svc;Endpoint=db.internal:5740"),
    ConnectionFactory = new TcpConnectionFactory(...),
});

await using var connection = await client.RentAsync();
var result = await connection.ExecuteAsync(
    "SELECT id, name FROM users WHERE id = @id",
    new Dictionary<string, object?> { ["id"] = 42 });
```

See [DESIGN.md](DESIGN.md) for the pooling and settings decisions.
