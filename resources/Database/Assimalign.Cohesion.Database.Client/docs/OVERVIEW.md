# Assimalign.Cohesion.Database.Client — Overview

The shared client core of the Data Platform: the protocol client every per-model
client (`Sql.Client`, `KeyValuePair.Client`, `Blob.Client`, …) builds on. It dials a
`libraries/Connections` transport, runs the startup/authenticate/ready
handshake, runs model-owned framed exchanges, and pools authenticated connections.

## Scope

- **`DatabaseClient`** — the sealed pooling entry point (`DatabaseClient.Create`):
  `RentAsync` returns an open, authenticated connection; disposing a rented
  connection returns it to the pool with its server session intact.
  `ExecuteStreamingAsync(DatabaseStreamingExchange)` rents on the caller's behalf
  and hands back a stream that owns the rental.
- **`DatabaseConnection`** — one sealed protocol session, rented already open:
  `ExecuteAsync<TResult>(DatabaseProtocolExchange<TResult>)` returns the model's
  result, and `ExecuteStreamingAsync(DatabaseStreamingExchange)` keeps a streaming
  exchange active while the caller reads; disposing the content stream cancels
  unfinished work before releasing the rental.
- **`DatabaseProtocolExchange<TResult>` and `DatabaseStreamingExchange`** — the
  abstract exchange bases a model client derives from. The exchange supplies codecs
  and materialization policy through protected cores; only the connection runs it.
- **`DatabaseClientOptions.Family`** — the model family fixed for every connection
  in the pool. The exchange must use the exact same family instance.
- **`DatabaseConnectionSettings`** — typed settings with a minimal `key=value;`
  connection-string parser (`Database`, `Principal`, `Endpoint=host[:port]`,
  `MaxPoolSize`), plus `For(Uri)` for generated or ambient resource
  endpoints.
- **`DatabaseClientException`** — the client error root, carrying the wire's
  stable `ProtocolErrorCode`. A failed dial carries `ConnectionFailure`, names the
  endpoint, and keeps the transport's exception as its inner exception.

## Dependencies

`Database` (root contracts + exception root), `Database.Protocol` (framing +
shared messages and family binding), `Connections` (transport factories).

## Usage

```csharp
// This example composes the SQL-owned extension from Database.Sql.Client.
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Client;

var client = DatabaseClient.Create(new DatabaseClientOptions
{
    Settings = DatabaseConnectionSettings.Parse("Database=app;Principal=svc;Endpoint=db.internal:5740"),
    ConnectionFactory = new TcpConnectionFactory(...),
    Family = SqlProtocol.Family,
});

await using var connection = await client.RentAsync();
var result = await connection.ExecuteAsync(
    "SELECT id, name FROM users WHERE id = @id",
    new Dictionary<string, object?> { ["id"] = 42 });
```

Generated and ambient endpoints stay typed:

```csharp
Uri endpoint = Resource.Endpoints.Db;
DatabaseConnectionSettings settings = DatabaseConnectionSettings.For(
    endpoint,
    database: "app",
    principal: Resource.Name);
```

`For(Uri)` requires a Cohesion endpoint URI—an absolute, host-bearing value with a valid port and
no user information, query, or fragment—and maps its `IdnHost` and `Port` directly to the socket
endpoint without formatting and reparsing a string.

See [DESIGN.md](DESIGN.md) for the pooling and settings decisions.


## Declarative commands

`DatabaseCommandClient.Create(Uri controlPlaneAddress, string bearerToken, HttpMessageInvoker transport)`
accepts a caller-owned transport, including its TLS trust policy. Disposing the returned client
does not dispose that transport; the caller retains it until requests finish and disposes it
afterward. The two-argument factory still creates a transport owned and disposed by the client.
Both overloads perform the same endpoint and credential validation; a null supplied transport
throws `ArgumentNullException`. The client does not discover application trust.

The sealed `DatabaseCommandClient` is the separate HTTP admin command client. `DatabaseCommandClient.Create`
accepts the full manifest control-plane URI (including its path) and an opaque bootstrap bearer.
SendCommandAsync posts the camel-case id/kind/owner/key/payload envelope to commands; payload is
base64. DeleteCommandAsync sends DELETE to the same route and envelope. Both return package-local
ResourceCommandObservation with Status and Detail, retaining actionable provider refusal text.
Transport failures propagate; HTTP refusals become Rejected observations. Serialization uses explicit
Utf8JsonWriter/JsonDocument access. The caller disposes the client; redirect following and cookies
are disabled. No runtime Hosting or ApplicationModel dependency was added.
