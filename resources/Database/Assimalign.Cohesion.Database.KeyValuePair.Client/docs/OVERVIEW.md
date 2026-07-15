# Assimalign.Cohesion.Database.KeyValuePair.Client — Overview

The typed key-value client: `IKeyValueClient`/`IKeyValueConnection`, a
point/range surface (get/put/delete/exists/scan with etag-conditional writes)
over the shared `Database.Client` pooling core.

## Purpose

Gives key-value consumers a typed wire client without any engine or language
reference: the connection builds the model's command grammar
(`GET @k`, `PUT @k @v [IF …]`, `DELETE @k [IF @etag]`, `EXISTS @k`,
`SCAN [FROM/TO/PREFIX/LIMIT]` — the contract in the engine package's
`docs/COMMANDS.md`) with byte parameters, sends it over the shared core, and
decodes the model's result shapes back into typed entries and outcomes.
Conditional misses (compare-and-swap) are first-class outcomes
(`KeyValueWriteResult`, `bool` returns) — never exceptions; failures map onto
the stable `KeyValueClientErrorKind` taxonomy with the wire code preserved.

## Scope

- `KeyValueClient.Create(options)` → pooling `IKeyValueClient`; rented
  `IKeyValueConnection`s return to the pool on dispose.
- `KeyValueClientEntry` (key/value/etag), `KeyValueWriteResult` (applied/etag),
  `KeyValueWriteCondition` (IfAbsent / IfETagMatches), `KeyValueScanRange`.
- `IKeyValueClientObserver` — the per-command telemetry hook (grammar text and
  counts only; key/value bytes never reach the observer).
- `KeyValueClientException` + `KeyValueClientErrorKind` — the error surface,
  with `ConnectionUsable` distinguishing command-level failures from broken
  connections.

## Dependencies

- `Assimalign.Cohesion.Database` — the area root (exception ancestry).
- `Assimalign.Cohesion.Database.Client` — the shared pooling/protocol core.
- `Assimalign.Cohesion.Database.Types` — the shared value codec (transitive wire
  encoding of parameters and rows).
- `Assimalign.Cohesion.Connections` — the transport factory that dials the server.

Deliberately no reference to the engine package (`Database.KeyValuePair`) and no
hosting reference — the client speaks the wire contract only.

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
