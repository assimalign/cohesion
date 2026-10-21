# Blob database engine

## Wire contract ownership

`Assimalign.Cohesion.Database.Blob.Protocol` owns this model's wire messages, codecs, family singleton and transferred value shapes (15 public types). Both engine and remote client reference that one assembly. Its public namespace and RootNamespace equal its project name. Engine requests, planning, catalog, storage and server lifecycle stay in `Assimalign.Cohesion.Database.Blob`. This extraction preserves wire bytes and validation; moved public types intentionally change namespace and assembly identity with no forwarders.


`Assimalign.Cohesion.Database.Blob` implements named databases, containers, and streamed
objects. Create an engine with `BlobDatabaseEngine.Create(name, options)`; a null `RootPath` selects memory,
and a path selects durable files. `AddBlob(name, engine => ...)` captures
construction through the root `IDatabaseApplicationBuilder` and returns that builder.
At Build the callback configures the sealed `BlobDatabaseEngineBuilder`: its `Options` (copied at
build), the databases it declares (`AddDatabase(name)`: opened or created by the build, never
dropped while declared), a server from options (`AddServer(options => ...)`), and deferred
nested worker and server factories typed over `BlobDatabaseEngine`. The application owns the
resulting engine and its nested components. The feature has no Hosting reference.

```csharp
await using var engine = BlobDatabaseEngine.Create("media-blob", new() { RootPath = "data" });
var database = await engine.CreateDatabaseAsync("media");
await using var session = await database.CreateSessionAsync();
var container = await session.CreateContainerAsync("images");
await using (var upload = await container.OpenWriteAsync("cover", new() { ContentType = "image/png" }))
{
    await source.CopyToAsync(upload);
}
await using var download = await container.OpenReadAsync("cover");
await download.CopyToAsync(destination);
```

Successful upload disposal publishes the completed content atomically. Flush writes buffered
chunks but does not publish the object. Failed or cancelled uploads abort. Readers retain the
selected version until their streams close. Listings use catalog metadata and optional ordinal
name prefixes. Empty objects, replacements, checksums, and persisted timestamps are supported.

Container operations exist only on the session (`session.CreateContainerAsync`,
`GetContainerAsync`, `DropContainerAsync`, `GetContainersAsync`; owner decision 32), and the
containers they return stay bound to that session. Disposing a database, directly or through
`session.Database`, closes it for every session; once the close ends the engine forgets it, and
`OpenDatabaseAsync` opens it again with its blobs, in memory as on disk (owner decision 33).

```csharp
await using var transaction = await session.BeginTransactionAsync();
var scopedContainer = await session.GetContainerAsync("images");
await using (var upload = await scopedContainer.OpenWriteAsync("cover"))
{
    await source.CopyToAsync(upload);
}
await transaction.CommitAsync();
```

Session operations use the active explicit transaction when present; otherwise each uses an
automatic transaction. The engine has one writer at a time, so a write from one session while
another session's explicit transaction has written waits for that transaction to end.
An operation that fails inside an explicit transaction aborts the whole transaction: the session
refuses further operations and BEGIN with `COHDBB001` until the caller rolls back, and a commit
fails without committing.
A failed journal or data fsync takes the database offline: every later operation, streams and
the server included, is refused with `DatabaseOfflineException` (`COHDBB002`) until
`OpenDatabaseAsync` reopens it and recovery decides the unconfirmed upload (#1243). While an
engine worker's work on a database keeps failing, the server refuses that database alone with
`COHDBB003` until the work succeeds or the engine gives up on the database
(`WorkerFailureWindow`, `WorkerFailureMinimumPasses`) and takes it offline.
`BufferPoolCapacity` (32 MiB), `CheckpointJournalSize` (256 MiB) and `CheckpointInterval`
(5 minutes) size the buffer pool and trigger checkpoints (#1254); a failed undo is retried on a
100 ms backoff (#1226). See DESIGN.md, "Storage operations".
Close each stream before starting another operation on the same session or committing.
Disposing a session aborts its pending work. Blob has no statement or query language:
`ExecuteAsync` rejects commands, including database switching and server administration.
Creating and dropping logical databases remains the host-side engine API.

The package also supplies `BlobDatabaseServer`, created with the engine and a
`BlobDatabaseServerOptions.Listener` implementing `IDuplexConnectionListener`. Call
`StartAsync` to bind and accept, then `StopAsync` to drain and terminally release the
listener. The composition root retains engine ownership. Startup binds each authenticated
session to one database; requests identify only containers and objects in that database.
The default authenticator trusts every principal; configure `Authenticator` for authenticated
access. Session limits, authentication deadlines, idle eviction, and bounded two-phase shutdown
are configurable. While a background worker fails on one database, the server refuses only that
database's handshakes and exchanges (`Unavailable`, `COHDBB003`) and serves the others; an
offline database is refused as offline (`COHDBB002`). Only a disposed engine, or one that has
failed as a whole (`DatabaseEngine.HasEngineWideFailure`), rejects every session, and a start
refused for that reason releases the listener and leaves the server stopped for good (owner
decision 42). A refused exchange, like every wire failure, ends the connection and aborts a
host-opened transaction first.

Blob.Protocol owns the Blob wire message family. Bind a `ProtocolChannel` to `BlobProtocol.Family`
at the Blob endpoint. `BlobReadMessage` and `BlobWriteMessage` identify an object;
`BlobProtocolTransfer.SendAsync` and `ReceiveAsync` copy its content using at most one 64 KiB
chunk in flight, with a receiver acknowledgement after each destination write. The helpers
accept non-seekable streams and unknown lengths. The caller supplies the shared handshake,
request dispatch, authentication, and upload publication when using the helpers directly.
The server supplies those responsibilities. The separate
[Blob.Client](../../Assimalign.Cohesion.Database.Blob.Client/docs/OVERVIEW.md) package provides
stream upload/download and typed delete, property, and prefix-listing operations over the shared
Database.Client connection. An upload is acknowledged only after complete content validation and
transaction commit. Cancellation, disconnect, malformed completion, or engine failure before
commit rolls back, preserving the previous object. A connection loss after commit but before its
acknowledgement leaves the caller uncertain whether publication succeeded. Verify properties
before retrying when that distinction matters.

Dependencies are Connections, the Database root, Database.Protocol, Blob.Storage, Blob.Catalog,
Database.Storage, and Database.Transactions. The implementation targets .NET 10, Preview C#, and NativeAOT without
reflection or Microsoft.Extensions packages. It references no concrete transport. Transport
construction belongs to the composition root; security policy beyond the supplied authenticator,
replication, hosting integration, and compiled-schema provisioning remain outside this package.

See [DESIGN.md](DESIGN.md), the [storage format](DESIGN.md#storage-component),
and the [catalog format](DESIGN.md#catalog-component).

`BlobDatabaseEngine.CreateBuilder(name)` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.

The engine, database, session, transaction, server, container and builder are sealed types; the
first five are leaves of the area root's bases (`DatabaseEngine`, `DatabaseInstance`,
`DatabaseSession`, `DatabaseTransaction`, `DatabaseServer`), which own the shared lifecycle, the
explicit-transaction state machine and their checks, so the typed members need no casts
(concrete-types plan, phase 4; DESIGN.md, "Concrete types").

`BlobDatabaseServerOptions.Listener` takes an `IDuplexConnectionListener`, including TCP and InMemory listeners. The server owns accepted duplex connections and their protocol I/O.

## Session capability (#1576, 2026-10-16)

`BlobDatabaseSession` derives directly from `DatabaseSession` for lifecycle, state and
explicit transactions. Blob operations use its container API. It has no execute member
and inherits no unsupported query-language operation.


## Catalog component

The following content moved from Assimalign.Cohesion.Database.Blob.Catalog; project and assembly references in this retained material now describe folders in Assimalign.Cohesion.Database.Blob. The [model component boundaries](DESIGN.md#model-component-boundaries) govern these components.

### Catalog: Blob catalog

`Assimalign.Cohesion.Database.Blob.Catalog` persists the containers and blob metadata of
one logical database through the sealed `BlobCatalog`, which `BlobCatalog.Open` returns; its
record and codec are internal. It depends on the Database root for the ownership vocabulary, Blob.Storage
for record access, and the shared Storage and Transactions kernels for durability and MVCC.

Open `BlobCatalog` after the database coordinator has scrubbed recovered records. Container
records carry a stable identity, case-sensitive name, `DatabaseObjectOwner`, and optional owning
schema. Blob records carry the container identity, name, length, media type, entity tag,
creation/modification times, content checksum, and chunk-chain head.

Supply a `TransactionContext` to every mutation and a `TransactionSnapshot` to every read.
The caller acquires the model locks and owns commit or rollback. The catalog never commits
the supplied transaction. Its metadata shares the content storage and journal so publication
of a new head and its chunks has one logical commit decision.

Blob listing supports an ordinal prefix and reads metadata records only. Ownership enforcement
belongs to the Blob engine; the catalog can persist a directly marked schema-owned container
without introducing compiled-schema provisioning.

See [DESIGN.md](DESIGN.md) for the directory, format, and recovery rules.


## Storage component

The following content moved from Assimalign.Cohesion.Database.Blob.Storage; project and assembly references in this retained material now describe folders in Assimalign.Cohesion.Database.Blob. The [model component boundaries](DESIGN.md#model-component-boundaries) govern these components.

### Storage: Blob storage

`Assimalign.Cohesion.Database.Blob.Storage` supplies chunk streams and the record
adapter used by the Blob engine and catalog. It references the shared
`Database.Storage` and `Database.Transactions` packages. Pages, page checksums,
free-space tracking, journal framing, physical recovery, locks, snapshots, and
logical recovery remain kernel responsibilities.

Create or open one `BlobStorage` file set, construct a `TransactionCoordinator`
with its `WriteAheadJournal` and `Records`, then perform the coordinator's normal
recovery sequence before accepting operations. Catalog records and chunks share
this file set and logical transaction. Metadata uses owner-zero pages, so catalog
listing does not scan blob content.

`OpenWrite` returns a forward-only stream holding one chunk buffer. Its completion
callback receives the chain head, length, and IEEE CRC-32 for atomic metadata
publication. `OpenRead` follows that reference one chunk at a time. An optional
disposal callback keeps the caller's snapshot pinned until the download closes.
Cancellation and failed uploads invoke the abort callback with the failure and cannot publish.

The [design](DESIGN.md) specifies the binary format and recovery invariants.
