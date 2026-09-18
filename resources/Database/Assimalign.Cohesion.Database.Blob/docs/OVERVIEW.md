# Blob database engine

`Assimalign.Cohesion.Database.Blob` implements named databases, containers, and streamed
objects. Create an engine with `BlobDatabaseEngine.Create`; a null `RootPath` selects memory,
and a path selects durable files. `AddBlobDatabase` registers the engine through the root
`IDatabaseApplicationBuilder` extension seam. The feature has no Hosting reference.

```csharp
await using var engine = BlobDatabaseEngine.Create(new() { RootPath = "data" });
var database = (IBlobDatabase)await engine.CreateDatabaseAsync("media");
var container = await database.CreateContainerAsync("images");
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

For explicit transactions, create a session and use the `IBlobDatabase` returned by its
existing `Database` property. Containers obtained from that view stay bound to the session.

```csharp
await using var session = await database.CreateSessionAsync();
await using var transaction = await session.BeginTransactionAsync();
var scopedDatabase = (IBlobDatabase)session.Database;
var scopedContainer = await scopedDatabase.GetContainerAsync("images");
await using (var upload = await scopedContainer.OpenWriteAsync("cover"))
{
    await source.CopyToAsync(upload);
}
await transaction.CommitAsync();
```

Direct database/container operations use automatic transactions. Session operations use the
active explicit transaction when present; otherwise they also use automatic transactions.
Close each stream before starting another operation on the same session or committing.
Disposing a session aborts its pending work. Blob has no statement or query language:
`ExecuteAsync` rejects commands, including database switching and server administration.
Creating and dropping logical databases remains the host-side engine API.

The package owns the Blob wire message family. Bind a `ProtocolChannel` to `BlobProtocol.Family`
at the Blob endpoint. `BlobReadMessage` and `BlobWriteMessage` identify an object;
`BlobProtocolTransfer.SendAsync` and `ReceiveAsync` copy its content using at most one 64 KiB
chunk in flight, with a receiver acknowledgement after each destination write. The helpers
accept non-seekable streams and unknown lengths. The caller supplies the shared handshake,
request dispatch, authentication, and upload publication. They are protocol building blocks
for the separate Blob client and server work, not a connection-owning client.

Dependencies are the Database root, Database.Protocol, Blob.Storage, Blob.Catalog,
Database.Storage, and Database.Transactions. The implementation targets .NET 10, Preview C#, and NativeAOT without
reflection or Microsoft.Extensions packages. Wire clients, security, replication, hosting
integration, and compiled-schema provisioning are outside this package's current scope.

See [DESIGN.md](DESIGN.md), the [storage format](../../Assimalign.Cohesion.Database.Blob.Storage/docs/DESIGN.md),
and the [catalog format](../../Assimalign.Cohesion.Database.Blob.Catalog/docs/DESIGN.md).
