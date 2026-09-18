# Blob wire client

`Assimalign.Cohesion.Database.Blob.Client` provides typed uploads, downloads, deletion,
properties, and prefix listing over the Blob wire family. It targets .NET 10 and NativeAOT,
uses explicit binary codecs, and requires neither reflection nor Microsoft.Extensions.

`BlobClient.Create` composes `DatabaseConnectionSettings` and an `IConnectionFactory`.
The shared `Database.Client` owns dialing, authentication, framing, and connection pooling;
this package owns Blob exchanges and the streaming API. The application supplies the
transport factory. Database and container provisioning remain server-side engine operations.

```csharp
await using var client = BlobClient.Create(new BlobClientOptions
{
    Settings = settings,
    ConnectionFactory = connectionFactory,
});
await using var connection = await client.ConnectAsync(cancellationToken);

long committed = await connection.UploadAsync("images", "cover", source,
    contentType: "image/png", cancellationToken: cancellationToken);
await using (Stream download = await connection.DownloadAsync("images", "cover", cancellationToken))
{
    await download.CopyToAsync(destination, cancellationToken);
}
BlobProperties? properties = await connection.GetPropertiesAsync("images", "cover", cancellationToken);
await foreach (BlobProperties item in connection.GetBlobsAsync("images", "covers/", cancellationToken))
{
    Console.WriteLine(item.Name);
}
bool deleted = await connection.DeleteAsync("images", "cover", cancellationToken);
```

Uploads read from the source's current position and leave it open. The source need not seek
or expose a length; use `length: -1` when the remaining length is unknown. A nonnegative
length must match exactly. `overwrite: false` rejects an existing name. Upload success means
the server acknowledged atomic publication, not merely receipt of the last chunk.

Downloads return a nonseekable read-only stream after validated transfer metadata arrives.
Read it to EOF and dispose it. Content is held in a bounded queue of 64 KiB chunks; neither
the client nor the server needs an object-sized buffer. Content larger than available memory
requires file-backed server storage. Both synchronous and asynchronous reads are supported.

One connection admits one exchange at a time. A live download or listing can hold its
exchange while waiting for the consumer. Dispose streams and enumerators before starting
another operation. Dispose rented connections before the client; healthy connections return
to the shared pool. Disposing a connection cancels and waits for its current exchange.

`BlobClientException.Code` preserves a server's stable `ProtocolErrorCode`; local truncation
or malformed frames map to `ProtocolViolation`, and transport failures map to `Internal`.
Every failed Blob exchange closes its connection, including rejected metadata operations.
Rent a fresh connection after failure. Missing metadata returns null and deletion of a
missing object returns false; downloading a missing object throws.

The token passed to `DownloadAsync` remains active after that method returns. A canceled
`ReadAsync` token cancels the entire download. Early stream or listing disposal aborts an
unfinished exchange. Server errors and truncation after download startup surface on reads;
they remain failures on subsequent reads and cannot become successful EOF. Bytes already
read before a later error are unverified partial content and must be discarded by the caller.

An interrupted upload before complete reception is rolled back and cannot expose a partial
object. Cancellation or connection loss after the server commits but before its acknowledgement
arrives has an uncertain outcome: the complete object may have been published. The protocol
does not provide exactly-once retry or a way to resolve that acknowledgement race.

See [DESIGN.md](DESIGN.md) for queue, pooling, and failure details, and the
[Blob engine wire specification](../../Assimalign.Cohesion.Database.Blob/docs/DESIGN.md#blob-wire-family)
for payload layouts.
