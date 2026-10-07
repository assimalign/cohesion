# Blob client API

`BlobClient.Create(BlobClientOptions)` returns the sealed `BlobClient`. Options require shared
`DatabaseConnectionSettings` and an `IConnectionFactory`; creating a client performs no I/O.
`ConnectAsync` rents an authenticated, sealed `BlobConnection` bound to the selected database.

| Member | Result and ownership |
| --- | --- |
| `UploadAsync` | Committed length after server publication acknowledgement; source remains open |
| `DownloadAsync` | Caller-owned nonseekable stream after validated start metadata |
| `GetPropertiesAsync` | Nullable `BlobProperties`; absence is null |
| `DeleteAsync` | True when deleted, false when absent |
| `GetBlobsAsync` | Async metadata sequence matching an optional ordinal prefix |
| `BlobConnection.DisposeAsync` | Cancels active exchange, waits, then returns or closes shared lease |
| `BlobClient.DisposeAsync` | Disposes the pool; rented connections should already be disposed |

`BlobClientException.Code` preserves a server `ProtocolErrorCode`; a failed dial in
`ConnectAsync` carries `ConnectionFailure`, with the transport's exception inside. Every failed exchange
invalidates its connection. Cancellation throws `OperationCanceledException`; overlapping
operations throw `InvalidOperationException`. A download's lifetime cancellation remains
active after return, and later read failures remain sticky. Successful EOF requires verified
completion. See the [streaming contract](../../OVERVIEW.md) and [design](../../DESIGN.md).
