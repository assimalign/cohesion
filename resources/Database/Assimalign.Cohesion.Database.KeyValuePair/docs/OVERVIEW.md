# Assimalign.Cohesion.Database.KeyValuePair — Overview

The key-value model engine: `KeyValueDatabaseEngine`, an ordered key space with
point operations (get/put/delete/exists), ordered range scans, and per-entry
etags for conditional writes — the second model engine on the shared database
kernel, and deliberately the kernel-generality proof (area DESIGN §3.10).

## Purpose

Implements the area root's `IDatabaseEngine`/`IDatabase`/`IDatabaseSession`
contracts for the key-value model by composing the shared kernel — storage
(pages/WAL/recovery), transactions (MVCC snapshots, lock manager), indexing
(B+Tree) — never re-implementing it. Keys and values are opaque byte sequences;
keys order by unsigned lexicographic byte comparison.

## Scope

- `KeyValueDatabaseEngine` + `KeyValueDatabaseEngineOptions` — the data machine:
  create → use → dispose, engine-owned background workers, two file sets per
  database (`<name>` + `<name>.catalog`).
- `IKeyValueDatabase` — the typed model surface (get/put/delete/exists/scan with
  etag-conditional writes).
- The typed request family (`KeyValueGetRequest`, `KeyValuePutRequest`,
  `KeyValueDeleteRequest`, `KeyValueExistsRequest`, `KeyValueScanRequest`) —
  the model's members of the shared `Database.Execution` request family.
- The text command grammar (`GET`/`PUT`/`DELETE`/`EXISTS`/`SCAN`) — the contract
  in [COMMANDS.md](COMMANDS.md), parsed by the session's text-execute seam so the
  wire protocol's Execute message serves the model with zero protocol changes.
- `KeyValueDatabaseServer` (+ `KeyValueDatabaseServerOptions`) — the model's
  wire-protocol server, a thin derivation of the shared server core
  (`Assimalign.Cohesion.Database.Server`); the second model server, whose
  construction fired the area's recorded extraction trigger (see DESIGN.md).

## Dependencies

- `Assimalign.Cohesion.Database` — the area root (contracts + child-root rollup).
- `Assimalign.Cohesion.Database.KeyValuePair.Storage` — the model's storage
  binding (`KeyValueStorage`).
- `Assimalign.Cohesion.Database.KeyValuePair.Catalog` — index registrations +
  entry-space format marker.
- `Assimalign.Cohesion.Database.Storage` / `.Types` — durability options surface
  and the shared tuple codec (direct references; the rest of the kernel arrives
  through the root).

## Usage

```csharp
await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions
{
    RootPath = "/var/lib/app/data", // omit for in-memory
});

var database = (IKeyValueDatabase)await engine.CreateDatabaseAsync("app");
await using var session = await database.CreateSessionAsync();

var put = await database.PutAsync(session, key, value);
var entry = await database.GetAsync(session, key);
var swap = await database.PutAsync(session, key, updated,
    new KeyValuePutOptions { ExpectedETag = entry?.ETag });
```

See [DESIGN.md](DESIGN.md) for the architecture and the decisions behind it.
