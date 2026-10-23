# Assimalign.Cohesion.Database.KeyValuePair — Overview

## Wire contract ownership

`Assimalign.Cohesion.Database.KeyValuePair.Protocol` owns this model's wire messages, codecs, family singleton and transferred value shapes (5 public types). Both engine and remote client reference that one assembly. Its public namespace and RootNamespace equal its project name. Engine requests, planning, catalog, storage and server lifecycle stay in `Assimalign.Cohesion.Database.KeyValuePair`. This extraction preserves wire bytes and validation; moved public types intentionally change namespace and assembly identity with no forwarders.


The key-value model engine: `KeyValueDatabaseEngine`, an ordered key space with
point operations (get/put/delete/exists), ordered range scans, and per-entry
etags for conditional writes — the second model engine on the shared database
kernel, and deliberately the kernel-generality proof (area DESIGN §3.10).

## Purpose

Derives sealed leaves from the area root's `DatabaseEngine`/`DatabaseInstance`/
`DatabaseExecutionSession<KeyValueRequest>`/`DatabaseTransaction`/`DatabaseServer` bases for the key-value
model (the concrete-first rule, `.claude/rules/database-area.md`) by composing the
shared kernel — storage
(pages/WAL/recovery), transactions (MVCC snapshots, lock manager), indexing
(B+Tree) — never re-implementing it. Keys and values are opaque byte sequences;
keys order by unsigned lexicographic byte comparison.

## Scope

- `KeyValueDatabaseEngine` + `KeyValueDatabaseEngineOptions` — the data machine:
  create → use → dispose, engine-owned background workers, two file sets per
  database (`keyvalue.*` + `catalog.*`, in a directory named for the lower case of the
  database's name, so every spelling of a name is one database; #1392). `KeyValueDatabaseEngineBuilder` (from
  `CreateBuilder(name)` or the `AddKeyValue(name, ...)` verb) carries the engine's `Options`
  (copied at build), the databases it declares (`AddDatabase(name)`: opened or created by the
  build, never dropped while declared), and its servers (`AddServer(options => ...)` or a
  factory) and workers. `KeyValueDatabaseEngine.Create(name, options)` creates a standalone
  engine; no options type carries the engine name. Open refuses a missing catalog with
  `CV06106` before opening either storage (#1238). File-system data of at most one page
  indicates interrupted creation and requires `DropDatabaseAsync`, then `CreateDatabaseAsync`;
  longer data with a missing catalog requires preserving storage and restoring the complete
  backup. Messages are storage-neutral, including in memory. Existing catalogs are validated before the
  data set opens or recovers (#1270); create still creates both file sets.
- `KeyValueDatabase` — the typed model surface (get/put/delete/exists/scan with
  etag-conditional writes); `KeyValueDatabaseSession` and
  `KeyValueDatabaseTransaction` are its typed session and explicit transaction.
- Explicit transactions: a failed command writes nothing and leaves the transaction
  active, so later commands stay inside it until the caller commits or rolls back. The
  exception is a storage, I/O or index failure (`CV06105`, or `CK06003` for corruption):
  it aborts the transaction, which then refuses commands and COMMIT with `CV04101` naming
  that cause until the caller rolls back (#1364). In autocommit the next command can run. A
  rollback can be repeated, and a started rollback always ends the transaction, even when
  its abort record or its undo fails (an undo that fails keeps the written keys locked until
  a retry completes it: about 100 ms later, then at doubling delays up to the maintenance
  interval, #1226; a failed journal write takes the database offline instead, #1252). A
  transaction the kernel ended under its
  caller refuses commands and COMMIT with `CV04101`. A rollback or a closed session ends
  the transaction even under a running command, which then fails and writes nothing; a
  commit while a command runs is refused.
- Sessions address exactly one database (A5). All five typed operations reject
  sessions from another database; text and typed requests execute only against
  the receiving session's database. Database/server administration stays on the
  host-owned engine and is absent from the command grammar.
- The typed request family (`KeyValueGetRequest`, `KeyValuePutRequest`,
  `KeyValueDeleteRequest`, `KeyValueExistsRequest`, `KeyValueScanRequest`) —
  the model's members of the shared `Database.Execution` request family.
- The text command grammar (`GET`/`PUT`/`DELETE`/`EXISTS`/`SCAN`) — the contract
  in [COMMANDS.md](COMMANDS.md), parsed by the internal `ExecuteCommandTextAsync` wire entry so the
  wire protocol's Execute message serves the model with zero protocol changes.
- `KeyValueDatabaseServer` (+ `KeyValueDatabaseServerOptions`) — the model's
  wire-protocol server, carrying its own full copy of the server machinery
  (servers are per-model and each model package owns its copy — owner decision
  2026-07-14; see DESIGN.md for the placement history).
- Storage operations: a failed journal or data fsync takes the database offline, both file
  sets at once, and every later operation, in process and over the server, is refused with
  `DatabaseOfflineException` (`CV06102`) until `OpenDatabaseAsync` reopens it and recovery
  decides the unconfirmed commit (#1243); `OfflineDatabases` feeds health. `BufferPoolCapacity` (32 MiB), `CheckpointJournalSize` (256 MiB) and
  `CheckpointInterval` (5 minutes) size the pool and trigger checkpoints (#1254). See
  DESIGN.md, "Storage operations".
- Closing: a database its holder disposed is forgotten once the close ends, and
  `OpenDatabaseAsync` opens it again with its entries, in memory as on disk (owner decision 33).

## Dependencies

- `Assimalign.Cohesion.Database` — the area root (contracts + child-root rollup).
- `Assimalign.Cohesion.Database.KeyValuePair.Protocol` — independently shared wire contracts.
- `Assimalign.Cohesion.Connections` — the transport abstraction for the model server.
- Storage and Catalog are components in this assembly (`src/Storage/` and `src/Catalog/`):
  the `KeyValueStorage` binding, index registrations and entry-space format marker.
- `Assimalign.Cohesion.Database.Storage` / `.Types` / `.Indexing` — durability options,
  the shared tuple codec and B+Tree (direct references; the rest of the kernel arrives
  through the root).
- `Assimalign.Cohesion.Connections` — the transport listeners the model's
  wire-protocol server binds and accepts from (the server owns listener
  lifecycle once start is attempted).

## Usage

```csharp
var builder = KeyValueDatabaseEngine.CreateBuilder("app-kv"); // the engine name, written once
builder.Options.RootPath = "/var/lib/app/data";               // omit for in-memory
builder.AddDatabase("app");                                     // opened, or created, by the build

await using var engine = await builder.BuildAsync();
var database = await engine.OpenDatabaseAsync("app"); // a KeyValueDatabase, no cast
await using var session = await database.CreateSessionAsync();

var put = await database.PutAsync(session, key, value);
var entry = await database.GetAsync(session, key);
var swap = await database.PutAsync(session, key, updated,
    new KeyValuePutOptions { ExpectedETag = entry?.ETag });
```

See [DESIGN.md](DESIGN.md) for the architecture and the decisions behind it.

`KeyValueDatabaseServerOptions.Listener` takes an `IDuplexConnectionListener`, including TCP and InMemory listeners. The server owns accepted duplex connections and their protocol I/O.

The public session accepts typed `KeyValueRequest` commands only. Its command grammar is an
internal wire entry (`ExecuteCommandTextAsync`), with validation and refusals before parsing,
then execution through the public typed NVI. Results use `DatabaseResult` and
`DatabaseResultSet`; [COMMANDS.md](COMMANDS.md) defines the wire order and reporting.


## Catalog component

The Catalog component is part of `Assimalign.Cohesion.Database.KeyValuePair`, with public types in `src/Catalog/` and helpers in `src/Internal/Catalog/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Catalog: KeyValuePair catalog overview

The key-value model's metadata catalog: the sealed `KeyValueCatalog`, opened over a
database's dedicated catalog storage file set (`KeyValueCatalog.Open`).

#### Catalog: Purpose

Persists exactly what re-attaches a key-value database on open:

- **Index registrations** — the physical identity (object id, definition, root
  page id) of the database's primary key index, exported by the index manager and
  re-persisted at the engine's persistence points (a backstop: the B+Tree keeps its
  root page fixed through splits since #1159).
- **Entry-space format version** — the data-storage layout marker (entry records
  and the primary index tree, 2 since #1194); neither is self-describing across
  format changes, so the engine reads this at open and refuses any format but its
  own.

#### Catalog: Scope

Deliberately minimal — the key-value model has no schemas, tables, columns, or
constraints beyond key uniqueness (enforced by the primary index itself, not
described here). Deferred model features with catalog surface area when they land:
named key spaces, per-entry expiration (TTL) metadata.

#### Catalog: Dependencies

- `Assimalign.Cohesion.Database` — the area root (`DatabaseException` ancestry).
- `Assimalign.Cohesion.Database.Storage` through the local `src/Storage/` component —
  the catalog file set the records persist on.
- `Assimalign.Cohesion.Database.Types` — the shared tuple codec records encode with.
- `Assimalign.Cohesion.Database.Indexing` — `BTreeIndexRegistration`.

#### Catalog: Usage

The engine (`Assimalign.Cohesion.Database.KeyValuePair`) opens one catalog per
database over its `catalog` file set; consumers never compose it directly.


## Storage component

The Storage component is part of `Assimalign.Cohesion.Database.KeyValuePair`, with public types in `src/Storage/` and helpers in `src/Internal/Storage/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Storage: KeyValuePair storage overview

The key-value model's storage binding: `KeyValueStorage`, the model-typed storage
file set (`StorageModel.KeyValue`) the key-value engine composes over the shared
`Database.Storage` kernel.

#### Storage: Purpose

`KeyValueStorage` derives from the kernel's `Storage` base and exposes exactly the
record surface the key-value engine needs — entry-record insert/read/update/delete
on the transactional page substrate, with owner-chain inserts keyed by the engine's
key-space object id (per-object page locality). No physical machinery lives here:
pages, buffer pool, free-space map, WAL, and recovery are the kernel's, unchanged.

#### Storage: Scope

- `KeyValueStorage` — factories (`Create`/`Open`, with the `checkpointOnOpen: false`
  open the engine's recovery analysis requires) and the entry-record operations.
- Nothing else. Entry encoding (version stamps + key/value tuple) belongs to the
  engine (`Assimalign.Cohesion.Database.KeyValuePair`), mirroring the SQL family
  split where `SqlRowCodec` lives in `Database.Sql`, not `Sql.Storage`.

#### Storage: Dependencies

- `Assimalign.Cohesion.Database.Storage` — the shared physical kernel (pages, WAL,
  recovery, transactional record surface).

#### Storage: Usage

The engine's storage strategies (`Assimalign.Cohesion.Database.KeyValuePair`'s
in-memory and file-system strategies) create and open instances; consumers never
compose this type directly.

Offline refusals and unconfirmed commits expose their existing model code through Diagnostics,
with unprefixed exception detail and the original cause. Failure tracing reads that carrier;
legacy wire error text keeps its existing code prefix. See DESIGN, structured failure codes (#1350).

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

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

All five server-options leaves inherit DatabaseServerOptions. Database.Tcp provides generic
Listen(Uri), retaining the concrete options type, and is public in App.Database (#1391, D7 B).
The SQL-specific TCP integration is removed; import Assimalign.Cohesion.Database for the helper.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.
Shared Engine source uses the consuming assembly's generated accessor through the internal `EngineErrorMessages` binding; no public resource accessor or cross-assembly internal access is introduced.

## KeyValuePair diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.


## Shared validation message completion (#1470)

Engine option requirements and portable database-name refusal reasons, including their rule
text, resolve through each consuming model's generated internal resources. Valid name checks
still avoid resource lookup; the engine-description template is cached for construction.
Existing message text, parameter names, refused values and pre-I/O validation order remain
unchanged. No public API, error identity, SQLSTATE, storage or wire format, event or counter changes.
Local predicate guards remain with the policies they validate; reusable unconditional catalog
throws use exception-type extension members. Private cold comparison and location-attachment
paths retain their allocation and exception-identity behavior.

## Internal convention maintenance

Existing-storage refusal text comes from a generated resource, preserving its text and exception contract. Internal placement and private naming follow the coding rules; see [DESIGN.md](DESIGN.md).

## Direct catalog diagnostics

`KeyValueCatalogException.Diagnostics` is non-null and preserves the existing catalog error identities
without changing exception ancestry or message text. See [DESIGN.md](DESIGN.md#direct-catalog-diagnostic-carriers)
for producer codes and the legacy public-constructor contract.

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

## Model failure producer completion

Session-state, result-row and model-specific refusals expose structured codes while preserving
their exception types and detail. The [producer table](DESIGN.md#model-failure-producer-completion)
lists each condition. Successful execution, transaction ownership, storage formats and wire
layouts are unchanged; the existing wire formatter includes an error code once.
Handshake refusal descriptions resolve through resources with their existing text and numeric
protocol error categories. Authentication/version negotiation and refusal control flow are unchanged.
