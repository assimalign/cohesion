# Blob database engine

`Assimalign.Cohesion.Database.Blob` implements named databases, containers, and streamed
objects. Create an engine with `BlobDatabaseEngine.Create`; a null `RootPath` selects memory,
and a path selects durable files. `AddBlob(name, engine => ...)` captures
construction through the root `IDatabaseApplicationBuilder` and returns that builder.
At Build the callback configures the sealed `BlobDatabaseEngineBuilder`, including deferred
nested worker and server factories typed over `BlobDatabaseEngine`. The application owns the
resulting engine and its nested components. The feature has no Hosting reference.

```csharp
await using var engine = BlobDatabaseEngine.Create(new() { RootPath = "data" });
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
`BlobDatabaseServerOptions.Listener` implementing `IConnectionListener`. Call
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

The package owns the Blob wire message family. Bind a `ProtocolChannel` to `BlobProtocol.Family`
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

See [DESIGN.md](DESIGN.md), the [storage format](../../Assimalign.Cohesion.Database.Blob.Storage/docs/DESIGN.md),
and the [catalog format](../../Assimalign.Cohesion.Database.Blob.Catalog/docs/DESIGN.md).

`BlobDatabaseEngine.CreateBuilder(name)` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.

The engine, database, session, transaction, server, container and builder are sealed types; the
first five are leaves of the area root's bases (`DatabaseEngine`, `DatabaseInstance`,
`DatabaseSession`, `DatabaseTransaction`, `DatabaseServer`), which own the shared lifecycle, the
explicit-transaction state machine and their checks, so the typed members need no casts
(concrete-types plan, phase 4; DESIGN.md, "Concrete types").
