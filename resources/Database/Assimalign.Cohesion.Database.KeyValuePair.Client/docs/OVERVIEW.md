# Assimalign.Cohesion.Database.KeyValuePair.Client — Overview

The typed key-value client: the sealed `KeyValueClient` and `KeyValueConnection`, a
point/range surface (get/put/delete/exists/scan with etag-conditional writes)
over the shared `Database.Client` pooling core.

## Purpose

Gives key-value consumers a typed wire client using the model-owned codecs:
the connection builds the model's command grammar
(`GET @k`, `PUT @k @v [IF …]`, `DELETE @k [IF @etag]`, `EXISTS @k`,
`SCAN [FROM/TO/PREFIX/LIMIT]` — the contract in the engine package's
`docs/COMMANDS.md`) with byte parameters, sends it over the shared core, and
decodes the model's result shapes back into typed entries and outcomes.
Conditional misses (compare-and-swap) are first-class outcomes
(`KeyValueWriteResult`, `bool` returns) — never exceptions; failures map onto
the stable `KeyValueClientErrorKind` taxonomy with the wire code preserved.

## Scope

- `KeyValueClient.Create(options)` → the sealed, pooling `KeyValueClient`; rented
  `KeyValueConnection`s return to the pool on dispose.
- `KeyValueClientEntry` (key/value/etag), `KeyValueWriteResult` (applied/etag),
  `KeyValueWriteCondition` (IfAbsent / IfETagMatches), `KeyValueScanRange`.
- `KeyValueClientObserver` — the abstract per-command telemetry hook (grammar text and
  counts only; key/value bytes never reach the observer).
- `KeyValueClientException` + `KeyValueClientErrorKind` — the error surface,
  with `ConnectionUsable` distinguishing command-level failures from broken
  connections.

## Dependencies

- `Assimalign.Cohesion.Database.Client` — the shared pooling/protocol core.
- `Assimalign.Cohesion.Database.KeyValuePair.Protocol` — shared names, family identifiers and payload codecs.
- `Assimalign.Cohesion.Database.Types` — the shared value codec (transitive wire
  encoding of parameters and rows).
- `Assimalign.Cohesion.Connections` — the transport factory that dials the server.

The shared Protocol reference supplies the wire contract; the client constructs no engine
and references no hosting module.

## Usage

```csharp
await using var client = KeyValueClient.Create(new KeyValueClientOptions
{
    Settings = DatabaseConnectionSettings.Parse("Database=app;Principal=svc;Endpoint=db-host:5333"),
    ConnectionFactory = new TcpConnectionFactory(),
});

await using var connection = await client.ConnectAsync();

long etag = await connection.PutAsync(key, value);
var entry = await connection.GetAsync(key);
var swap = await connection.PutAsync(key, updated, KeyValueWriteCondition.IfETagMatches(entry!.Value.ETag));
var page = await connection.ScanAsync(new KeyValueScanRange { Prefix = prefix, Limit = 100 });
```

Supply an `IDuplexConnectionFactory` through `KeyValueClientOptions.ConnectionFactory`; the shared pool owns the duplex connections it establishes.

## KeyValuePair diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Debugger inspection (#1470)

Client debugger inspection shows stored metadata and owned-array counts without transport work, enumeration or decoding.

Eligible types follow the [Database conventions](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md) debugger display/proxy rules. Views are private and read-only; inspection does not change the public API, execution paths, error contracts, storage formats or protocol bytes.

## Registered scalar CALL

`ValueTask<object?> KeyValueConnection.InvokeFunctionAsync(KeyValueFunctionName name,
IReadOnlyList<object?> arguments, CancellationToken cancellationToken = default)` constructs
`CALL name @p0 @p1` with invariant ordinal parameter names and existing Execute frames.
Names use the shared Protocol value object; default names and unsupported CLR values are
refused locally. Arguments are exact native primitives (NULL through Guid, codec tags 0–16),
with no widening. Binary arguments are copied and binary results are caller-owned byte arrays.
The response must have one primitive `value` column, exactly one row and affected count -1.

The server owns registrations, overload selection, NULL handling and execution. Resolution
CV01018–21, callback CV05003 and declared-result CV02016 retain ExecutionFailure and the code
once in text; ordinary refusals keep the connection usable. The callback is trusted application
code, not sandboxed. Cancellation and malformed/framing failures retain existing pool policy.
The client imports only KV.Protocol for the name and wire contracts: no engine, function catalog,
storage, transaction or dynamic/reflection dependency is added. No new message or wire version.

An already-canceled CALL token is observed after disposed/name/list guards and before command
construction, binary copying, telemetry or exchange admission. Its OperationCanceledException
retains the caller's token and leaves the rental healthy. Once an exchange is admitted,
existing linked-token, cancellation and pool policy applies. The six existing client command
methods and the shared client core are unchanged.
