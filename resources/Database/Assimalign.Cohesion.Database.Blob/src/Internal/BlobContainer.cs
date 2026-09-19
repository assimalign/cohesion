using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobContainer(BlobDatabaseInstance database, BlobContainerMetadata container, BlobDatabaseSession? session) : IBlobContainer
{
    public string Name => container.Name;

    internal ValueTask<IReadOnlyDictionary<string, object?>> GetOwnershipAsync(CancellationToken cancellationToken)
        => database.RunAsync(session, operation =>
        {
            EnsureContainer(operation.Context);
            var current = database.Catalog.FindContainer(container.Name, operation.Context.Snapshot)!.Value;
            return new ValueTask<IReadOnlyDictionary<string, object?>>(
                new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["OWNER"] = current.Owner,
                    ["OWNING_SCHEMA"] = current.OwningSchema,
                }));
        }, cancellationToken);

    public async ValueTask<Stream> OpenWriteAsync(string name, BlobWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool overwrite = options?.Overwrite ?? true;
        string? contentType = options?.ContentType;
        var operation = await database.BeginOperationAsync(session, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureContainer(operation.Context, writing: true);
            var previous = database.Catalog.FindBlob(container.Id, name, operation.Context.Snapshot);
            if (previous != database.Catalog.FindBlob(container.Id, name, database.LatestSnapshot(operation.Context)))
            {
                BlobDatabaseInstance.ThrowConflict();
            }

            if (previous is not null && !overwrite)
            {
                throw new DatabaseException($"Blob '{name}' already exists.");
            }

            var stream = database.DataStorage.OpenWrite(database.Coordinator, operation.Context, async content =>
            {
                operation.EnsureActive();
                cancellationToken.ThrowIfCancellationRequested();
                if (previous is not null)
                {
                    await database.DataStorage.TombstoneContentAsync(database.Coordinator, operation.Context,
                        BlobDatabaseInstance.Content(previous.Value), cancellationToken).ConfigureAwait(false);
                }

                var now = DateTimeOffset.UtcNow;
                // Physical sequence allocation shares the durable allocator with
                // the coordinator, so even repeated writes in one transaction
                // receive distinct tags and no tag repeats after a restart.
                ulong etag = (ulong)database.DataStorage.ReserveTransactionSequence();
                await database.Catalog.SaveBlobAsync(new BlobCatalogEntry(container.Id, name, content.Length, contentType,
                    etag, previous?.CreatedAt ?? now, now, content.Checksum, content.Head), operation.Context, cancellationToken).ConfigureAwait(false);
                await operation.CompleteAsync().ConfigureAwait(false);
            }, operation.AbortAsync, cancellationToken);
            return new BlobGuardedStream(stream, operation);
        }
        catch { await operation.AbortAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask<Stream> OpenReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = await database.BeginOperationAsync(session, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureContainer(operation.Context);
            var entry = database.Catalog.FindBlob(container.Id, name, operation.Context.Snapshot)
                ?? throw new DatabaseException($"Blob '{name}' does not exist.");
            return new BlobGuardedStream(database.DataStorage.OpenRead(BlobDatabaseInstance.Content(entry), operation.CompleteAsync), operation);
        }
        catch { await operation.AbortAsync().ConfigureAwait(false); throw; }
    }

    public ValueTask<BlobProperties?> GetPropertiesAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return database.RunAsync(session, operation =>
        {
            EnsureContainer(operation.Context);
            var entry = database.Catalog.FindBlob(container.Id, name, operation.Context.Snapshot);
            return new ValueTask<BlobProperties?>(entry is null ? null : BlobDatabaseInstance.Properties(entry.Value));
        }, cancellationToken);
    }

    public ValueTask<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return database.RunAsync(session, async operation =>
        {
            await database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureContainer(operation.Context, writing: true);
            var entry = database.Catalog.FindBlob(container.Id, name, operation.Context.Snapshot);
            if (entry != database.Catalog.FindBlob(container.Id, name, database.LatestSnapshot(operation.Context)))
            {
                BlobDatabaseInstance.ThrowConflict();
            }

            if (entry is null)
            {
                return false;
            }

            await database.DataStorage.TombstoneContentAsync(database.Coordinator, operation.Context,
                BlobDatabaseInstance.Content(entry.Value), cancellationToken).ConfigureAwait(false);
            await database.Catalog.DeleteBlobAsync(container.Id, name, operation.Context, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public async IAsyncEnumerable<BlobProperties> GetBlobsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entries = await database.RunAsync(session, operation =>
        {
            EnsureContainer(operation.Context);
            return new ValueTask<IReadOnlyList<BlobCatalogEntry>>(database.Catalog.GetBlobs(container.Id, prefix, operation.Context.Snapshot));
        }, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            database.ThrowIfDisposed();
            session?.ThrowIfNotOpen();
            yield return BlobDatabaseInstance.Properties(entry);
        }
    }

    private void EnsureContainer(ITransactionContext context, bool writing = false)
    {
        var current = database.Catalog.FindContainer(Name, context.Snapshot);
        if (current?.Id != container.Id)
        {
            throw new DatabaseException($"Container '{Name}' is no longer available in this transaction.");
        }

        if (writing && current != database.Catalog.FindContainer(Name, database.LatestSnapshot(context)))
        {
            BlobDatabaseInstance.ThrowConflict();
        }
    }
}

// Keeps a read snapshot pinned until disposal and refuses access after session
// rollback/disposal, including bytes already buffered by the underlying stream.
internal sealed class BlobGuardedStream(Stream inner, BlobOperation operation) : Stream
{
    private bool _disposed;
    public override bool CanRead => !_disposed && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && inner.CanWrite;
    public override long Length { get { Check(); return inner.Length; } }
    public override long Position { get { Check(); return inner.Position; } set => throw new NotSupportedException(); }
    public override void Flush() { Check(); inner.Flush(); }
    public override Task FlushAsync(CancellationToken cancellationToken) { Check(); return inner.FlushAsync(cancellationToken); }
    public override int Read(byte[] buffer, int offset, int count) { Check(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { Check(); return inner.Read(buffer); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(); return inner.ReadAsync(buffer, cancellationToken); }
    public override void Write(byte[] buffer, int offset, int count) { Check(); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(); inner.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(); return inner.WriteAsync(buffer, cancellationToken); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); operation.EnsureActive(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { inner.Dispose(); }
            catch { operation.AbortAsync().AsTask().GetAwaiter().GetResult(); throw; }
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { await inner.DisposeAsync().ConfigureAwait(false); }
        catch { await operation.AbortAsync().ConfigureAwait(false); throw; }
        GC.SuppressFinalize(this);
    }
}
