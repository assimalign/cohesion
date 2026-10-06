using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Catalog;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// A blob container whose every operation runs in autocommit, in a session of its own: what a
/// test that does not exercise sessions uses since owner decision 32 of 2026-10-06 made every
/// container operation a session's (the database's own container operations, which ran in
/// autocommit outside any session, are gone). It keeps the container's identity, so a handle of a
/// dropped container never addresses one created later under its name, as the database's own
/// handle did not. A stream keeps its session until the stream is disposed.
/// </summary>
internal sealed class AutocommitContainer
{
    private readonly BlobDatabase _database;
    private readonly BlobContainerMetadata _metadata;

    private AutocommitContainer(BlobDatabase database, BlobContainerMetadata metadata)
    {
        _database = database;
        _metadata = metadata;
    }

    /// <summary>Gets the name of the container.</summary>
    public string Name => _metadata.Name;

    /// <summary>Creates a container in an autocommit operation of a session of its own.</summary>
    /// <param name="database">The database.</param>
    /// <param name="name">The container's name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created container.</returns>
    public static async Task<AutocommitContainer> CreateAsync(BlobDatabase database, string name, CancellationToken cancellationToken = default)
    {
        await using var session = await database.CreateSessionAsync(cancellationToken);
        var container = await session.CreateContainerAsync(name, cancellationToken);
        return new AutocommitContainer(database, container.Metadata);
    }

    /// <summary>Opens an existing container in an autocommit operation of a session of its own.</summary>
    /// <param name="database">The database.</param>
    /// <param name="name">The container's name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The container.</returns>
    public static async Task<AutocommitContainer> GetAsync(BlobDatabase database, string name, CancellationToken cancellationToken = default)
    {
        await using var session = await database.CreateSessionAsync(cancellationToken);
        var container = await session.GetContainerAsync(name, cancellationToken);
        return new AutocommitContainer(database, container.Metadata);
    }

    /// <summary>Drops a container in an autocommit operation of a session of its own.</summary>
    /// <param name="database">The database.</param>
    /// <param name="name">The container's name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the container is dropped.</returns>
    public static async Task DropAsync(BlobDatabase database, string name, CancellationToken cancellationToken = default)
    {
        await using var session = await database.CreateSessionAsync(cancellationToken);
        await session.DropContainerAsync(name, cancellationToken);
    }

    /// <summary>Lists the containers' names in an autocommit operation of a session of its own.</summary>
    /// <param name="database">The database.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The names, in ordinal order.</returns>
    public static async Task<List<string>> NamesAsync(BlobDatabase database, CancellationToken cancellationToken = default)
    {
        await using var session = await database.CreateSessionAsync(cancellationToken);
        var names = new List<string>();
        await foreach (var container in session.GetContainersAsync(cancellationToken))
        {
            names.Add(container.Name);
        }

        return names;
    }

    /// <inheritdoc cref="BlobContainer.OpenWriteAsync"/>
    public ValueTask<Stream> OpenWriteAsync(string name, BlobWriteOptions? options = null, CancellationToken cancellationToken = default)
        => OpenAsync(container => container.OpenWriteAsync(name, options, cancellationToken), cancellationToken);

    /// <inheritdoc cref="BlobContainer.OpenReadAsync"/>
    public ValueTask<Stream> OpenReadAsync(string name, CancellationToken cancellationToken = default)
        => OpenAsync(container => container.OpenReadAsync(name, cancellationToken), cancellationToken);

    /// <inheritdoc cref="BlobContainer.GetPropertiesAsync"/>
    public async ValueTask<BlobProperties?> GetPropertiesAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var session = await _database.CreateSessionAsync(cancellationToken);
        return await Bind(session).GetPropertiesAsync(name, cancellationToken);
    }

    /// <inheritdoc cref="BlobContainer.DeleteAsync"/>
    public async ValueTask<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var session = await _database.CreateSessionAsync(cancellationToken);
        return await Bind(session).DeleteAsync(name, cancellationToken);
    }

    /// <inheritdoc cref="BlobContainer.GetBlobsAsync"/>
    public async IAsyncEnumerable<BlobProperties> GetBlobsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var session = await _database.CreateSessionAsync(cancellationToken);
        await foreach (var blob in Bind(session).GetBlobsAsync(prefix, cancellationToken))
        {
            yield return blob;
        }
    }

    /// <inheritdoc cref="BlobContainer.GetOwnershipAsync"/>
    public async ValueTask<IReadOnlyDictionary<string, object?>> GetOwnershipAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _database.CreateSessionAsync(cancellationToken);
        return await Bind(session).GetOwnershipAsync(cancellationToken);
    }

    // The container's identity, bound to a session.
    private BlobContainer Bind(BlobDatabaseSession session) => new(_database, _metadata, session);

    private async ValueTask<Stream> OpenAsync(Func<BlobContainer, ValueTask<Stream>> open, CancellationToken cancellationToken)
    {
        var session = await _database.CreateSessionAsync(cancellationToken);
        try
        {
            return new SessionStream(await open(Bind(session)), session);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    // A blob stream that closes its session once the stream is disposed.
    private sealed class SessionStream : Stream
    {
        private readonly Stream _inner;
        private readonly BlobDatabaseSession _session;

        public SessionStream(Stream inner, BlobDatabaseSession session)
        {
            _inner = inner;
            _session = session;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync();
            }
            finally
            {
                await _session.DisposeAsync();
            }

            GC.SuppressFinalize(this);
        }
    }
}
