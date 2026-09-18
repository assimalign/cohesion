# Blob client API

`BlobClient.Create(BlobClientOptions)` returns an `IBlobClient`. Options require shared
`DatabaseConnectionSettings` and an `IConnectionFactory`; creating a client performs no I/O.
`ConnectAsync` rents an authenticated `IBlobConnection` bound to the selected database.

| Member | Result and ownership |
| --- | --- |
| `UploadAsync` | Committed length after server publication acknowledgement; source remains open |
| `DownloadAsync` | Caller-owned nonseekable stream after validated start metadata |
| `GetPropertiesAsync` | Nullable `BlobProperties`; absence is null |
| `DeleteAsync` | True when deleted, false when absent |
| `GetBlobsAsync` | Async metadata sequence matching an optional ordinal prefix |
| `IBlobConnection.DisposeAsync` | Cancels active exchange, waits, then returns or closes shared lease |
| `IBlobClient.DisposeAsync` | Disposes the pool; rented connections should already be disposed |

`BlobClientException.Code` preserves a server `ProtocolErrorCode`. Every failed exchange
invalidates its connection. Cancellation throws `OperationCanceledException`; overlapping
operations throw `InvalidOperationException`. A download's lifetime cancellation remains
active after return, and later read failures remain sticky. Successful EOF requires verified
completion. See the [streaming contract](../../OVERVIEW.md) and [design](../../DESIGN.md).
