# IFileSystemFileHandle

Namespace: `Assimalign.Cohesion.FileSystem`  
Assembly: `Assimalign.Cohesion.FileSystem`

A caller-owned random-access file handle implementing `IDisposable` and `IAsyncDisposable`.
Obtain one from [`IFileSystemFile.OpenHandle`](../IFileSystemFile/OVERVIEW.md) with explicit
`FileMode`, `FileAccess`, and `FileShare`. The provider owns opening and access checks;
the caller owns disposal. There is no public constructor.

## Surface

| Member | Behavior |
|--------|----------|
| `long Length { get; }` | Current byte length, including writes and `SetLength` changes. |
| `bool SupportsDurableFlush { get; }` | Whether this backing file can flush to durable storage. |
| `int Read(Span<byte> buffer, long offset)` | Reads at a byte offset; returns bytes read, possibly fewer at EOF. |
| `ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)` | Async positional read with cancellation. |
| `void Write(ReadOnlySpan<byte> buffer, long offset)` | Writes at a byte offset, extending the file if necessary. |
| `ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)` | Async positional write with cancellation. |
| `void SetLength(long length)` | Truncates or extends the file to the requested length. |
| `void Flush(bool durable)` | Flushes buffers; `true` additionally requires reaching durable storage. |
| `ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)` | Async flush with the same durability contract and cancellation. |
| `void Dispose()` / `ValueTask DisposeAsync()` | Releases the handle and its sharing registration; repeated disposal is harmless. |

## Positional and durability contracts

Each read or write addresses its own offset, so concurrent operations do not interfere through
a shared cursor. Overlapping writes still require application coordination. `Stream` cannot
express this contract or durable flush, which is why storage engines need the handle surface.

When `SupportsDurableFlush` is `false`, `Flush(durable: true)` and
`FlushAsync(durable: true)` throw `NotSupportedException`. A storage engine that asks for
durability and silently does not get it is worse than one that cannot start. Choose relaxed
flush explicitly only when persistence is not required.

Physical and IsolatedStorage handles support durable flush. InMemory handles do not. Aggregate
returns the resolved file's handle, so its capability can differ between files in different
mounts. `Flush(false)` makes no guarantee about persistent storage.

## Lifetime and exceptions

Operations on a disposed handle throw `ObjectDisposedException`. Negative offsets and lengths
are invalid. Access, sharing, file mode, size limits, and I/O errors follow the backing
provider. Canceled asynchronous operations raise `OperationCanceledException`; cancellation
does not roll back writes that have already completed.

## Example

```csharp
await using IFileSystemFileHandle handle = file.OpenHandle(
    FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

await handle.WriteAsync(page, offset: 4096, cancellationToken);
await handle.FlushAsync(durable: true, cancellationToken);
```

Here `file` is an `IFileSystemFile`, `page` is a `ReadOnlyMemory<byte>`, and
`cancellationToken` comes from the caller. This example deliberately requires durability and
fails on a non-durable backing provider.
