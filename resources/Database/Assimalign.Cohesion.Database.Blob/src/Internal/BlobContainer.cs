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

internal sealed class BlobContainer : IBlobContainer
{
    private readonly BlobDatabaseInstance _database;
    private readonly BlobContainerMetadata _container;
    private readonly BlobDatabaseSession? _session;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobContainer"/> class.
    /// </summary>
    /// <param name="database">The blob database instance that owns the container.</param>
    /// <param name="container">The catalog metadata of the container.</param>
    /// <param name="session">The session that scopes container operations, or <see langword="null"/> for autonomous operations.</param>
    public BlobContainer(BlobDatabaseInstance database, BlobContainerMetadata container, BlobDatabaseSession? session)
    {
        _database = database;
        _container = container;
        _session = session;
    }

    public string Name => _container.Name;

    internal ValueTask<IReadOnlyDictionary<string, object?>> GetOwnershipAsync(CancellationToken cancellationToken)
        => _database.RunAsync(_session, operation =>
        {
            EnsureContainer(operation.Context);
            var current = _database.Catalog.FindContainer(_container.Name, operation.Context.Snapshot)!.Value;
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
        var operation = await _database.BeginOperationAsync(_session, cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureContainer(operation.Context, writing: true);
            var previous = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot);
            if (previous != _database.Catalog.FindBlob(_container.Id, name, _database.LatestSnapshot(operation.Context)))
            {
                BlobDatabaseInstance.ThrowConflict();
            }

            if (previous is not null && !overwrite)
            {
                throw new DatabaseException($"Blob '{name}' already exists.");
            }

            var stream = _database.DataStorage.OpenWrite(_database.Coordinator, operation.Context, async content =>
            {
                operation.EnsureActive();
                cancellationToken.ThrowIfCancellationRequested();
                if (previous is not null)
                {
                    await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context,
                        BlobDatabaseInstance.Content(previous.Value), cancellationToken).ConfigureAwait(false);
                }

                var now = DateTimeOffset.UtcNow;
                // Physical sequence allocation shares the durable allocator with
                // the coordinator, so even repeated writes in one transaction
                // receive distinct tags and no tag repeats after a restart.
                ulong etag = (ulong)_database.DataStorage.ReserveTransactionSequence();
                await _database.Catalog.SaveBlobAsync(new BlobCatalogEntry(_container.Id, name, content.Length, contentType,
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
        var operation = await _database.BeginOperationAsync(_session, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureContainer(operation.Context);
            var entry = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot)
                ?? throw new DatabaseException($"Blob '{name}' does not exist.");
            return new BlobGuardedStream(_database.DataStorage.OpenRead(BlobDatabaseInstance.Content(entry), operation.CompleteAsync), operation);
        }
        catch { await operation.AbortAsync().ConfigureAwait(false); throw; }
    }

    public ValueTask<BlobProperties?> GetPropertiesAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _database.RunAsync(_session, operation =>
        {
            EnsureContainer(operation.Context);
            var entry = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot);
            return new ValueTask<BlobProperties?>(entry is null ? null : BlobDatabaseInstance.Properties(entry.Value));
        }, cancellationToken);
    }

    public ValueTask<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _database.RunAsync(_session, async operation =>
        {
            await _database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureContainer(operation.Context, writing: true);
            var entry = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot);
            if (entry != _database.Catalog.FindBlob(_container.Id, name, _database.LatestSnapshot(operation.Context)))
            {
                BlobDatabaseInstance.ThrowConflict();
            }

            if (entry is null)
            {
                return false;
            }

            await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context,
                BlobDatabaseInstance.Content(entry.Value), cancellationToken).ConfigureAwait(false);
            await _database.Catalog.DeleteBlobAsync(_container.Id, name, operation.Context, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public async IAsyncEnumerable<BlobProperties> GetBlobsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entries = await _database.RunAsync(_session, operation =>
        {
            EnsureContainer(operation.Context);
            return new ValueTask<IReadOnlyList<BlobCatalogEntry>>(_database.Catalog.GetBlobs(_container.Id, prefix, operation.Context.Snapshot));
        }, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _database.ThrowIfDisposed();
            _session?.ThrowIfNotOpen();
            yield return BlobDatabaseInstance.Properties(entry);
        }
    }

    private void EnsureContainer(ITransactionContext context, bool writing = false)
    {
        var current = _database.Catalog.FindContainer(Name, context.Snapshot);
        if (current?.Id != _container.Id)
        {
            throw new DatabaseException($"Container '{Name}' is no longer available in this transaction.");
        }

        if (writing && current != _database.Catalog.FindContainer(Name, _database.LatestSnapshot(context)))
        {
            BlobDatabaseInstance.ThrowConflict();
        }
    }
}

// Keeps a read snapshot pinned until disposal and refuses access after session
// rollback/disposal, including bytes already buffered by the underlying stream.
internal sealed class BlobGuardedStream : Stream
{
    private readonly Stream _inner;
    private readonly BlobOperation _operation;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobGuardedStream"/> class.
    /// </summary>
    /// <param name="inner">The underlying blob content stream.</param>
    /// <param name="operation">The blob operation whose transaction must stay active for the stream to be used.</param>
    public BlobGuardedStream(Stream inner, BlobOperation operation)
    {
        _inner = inner;
        _operation = operation;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && _inner.CanWrite;
    public override long Length { get { Check(); return _inner.Length; } }
    public override long Position { get { Check(); return _inner.Position; } set => throw new NotSupportedException(); }
    public override void Flush() { Check(); _inner.Flush(); }
    public override Task FlushAsync(CancellationToken cancellationToken) { Check(); return _inner.FlushAsync(cancellationToken); }
    public override int Read(byte[] buffer, int offset, int count) { Check(); return _inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { Check(); return _inner.Read(buffer); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(); return _inner.ReadAsync(buffer, cancellationToken); }
    public override void Write(byte[] buffer, int offset, int count) { Check(); _inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(); _inner.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(); return _inner.WriteAsync(buffer, cancellationToken); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); _operation.EnsureActive(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _inner.Dispose(); }
            catch { _operation.AbortAsync().AsTask().GetAwaiter().GetResult(); throw; }
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
        try { await _inner.DisposeAsync().ConfigureAwait(false); }
        catch { await _operation.AbortAsync().ConfigureAwait(false); throw; }
        GC.SuppressFinalize(this);
    }
}
