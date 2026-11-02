# Assimalign.Cohesion.Database.Client — Overview

The shared client core of the Data Platform: the protocol client every per-model
client (`Sql.Client`, `KeyValuePair.Client`, `Blob.Client`, …) builds on. It dials a
`libraries/Connections` transport, runs the startup/authenticate/ready
handshake, runs model-owned framed exchanges, and pools authenticated connections.
SQL, Graph and Documents negotiate transaction status so pool return can roll back open or failed
work and require Idle before reuse. Legacy transaction-capable peers without status are discarded.

## Scope

- **`DatabaseClient`** — the sealed pooling entry point (`DatabaseClient.Create`):
  `RentAsync` returns a fresh lease over an open, authenticated session; disposing it
  permanently ends that lease and returns its healthy server session to the pool.
  `ExecuteStreamingAsync(DatabaseStreamingExchange)` rents on the caller's behalf
  and hands back a stream that owns the rental.
- **`DatabaseConnection`** — one sealed rental lease, handed out already open:
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
  `MaxPoolSize`), plus `For(Uri)` for configured Database wire
  endpoints.
- **`DatabaseClientException`** — the client error root, carrying the wire's
  stable `ProtocolErrorCode`. A failed dial carries `ConnectionFailure`, names the
  endpoint, and keeps the transport's exception as its inner exception.

## Dependencies

`Database.Types` (`DatabaseType` identities and `Diagnostic`), `Database.Protocol`
(framing + shared messages and family binding), `Connections` (transport factories).
It does not reference the `Database` root: `DatabaseClientException` is its own
exception root.

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

Read the Database wire address from configuration and keep it typed:

```csharp
Uri endpoint = new Uri(configuration["Database:Endpoint"]!);
DatabaseConnectionSettings settings = DatabaseConnectionSettings.For(
    endpoint,
    database: "app",
    principal: "orders-api");
```

`For(Uri)` requires a Cohesion endpoint URI—an absolute, host-bearing value with a valid port and
no user information, query, or fragment—and maps its `IdnHost` and `Port` directly to the socket
endpoint without formatting and reparsing a string.

See [DESIGN.md](DESIGN.md) for the pooling and settings decisions.


Supply an `IDuplexConnectionFactory` through `DatabaseClientOptions.ConnectionFactory`; the shared pool owns the duplex connections it establishes.

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

Pool return now rolls back unfinished SQL/Graph transactions using negotiated server status before reusing a session; uncertified sessions are discarded (#1540).

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.

## Explicit pool-reset construction

The internal pool-reset exchange uses an explicit constructor while retaining the same negotiated status and connection-reuse behavior; see [DESIGN.md](DESIGN.md).


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Lease getters that take locks or enforce disposal, delegated stream state and transport callbacks are omitted. Stored connection metadata is exposed without I/O or exchange activity.
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.


## Function exchange canary

The client-only NativeAOT sample exercises `KeyValueConnection.InvokeFunctionAsync` against an
in-memory protocol peer for an Int64 value, NULL, and binary data. It verifies parameterized CALL
text, typed component encoding, result headers and materialized values through the existing
Execute/result frames. The pre-trim reference-closure guard still excludes engine, parser,
storage and transaction assemblies; the canary gains no engine reference.

## Lease cancellation completion

Disposing or aborting an active rental joins its queued cancellation callbacks before exchange
cleanup disposes their source. Cancellation and cleanup claim ownership under the lease lock;
callbacks, source disposal and pool return run outside it. Callback failures still reach the
returning caller after the exchange and pool slot are released, while the exchange retains its
original result or cancellation exception. Repeated disposal joins the same return task. This
repairs the #1313 exchange-join guarantee without changing public APIs or wire behavior; see
[DESIGN.md](DESIGN.md#lease-cancellation-completion).
