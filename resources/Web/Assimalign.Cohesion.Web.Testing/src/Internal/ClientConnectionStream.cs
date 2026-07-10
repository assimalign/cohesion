using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Web.Testing.Internal;

/// <summary>
/// The client-side stream handed to <see cref="System.Net.Http.SocketsHttpHandler"/>'s
/// <c>ConnectCallback</c>: adapts a dialed in-memory connection's duplex pipe as a
/// <see cref="Stream"/> and takes ownership of the connection, so the HTTP client pool
/// releasing the stream gracefully closes the connection — the server end observes
/// end-of-stream and unwinds its receive loop instead of parking until server shutdown.
/// </summary>
internal sealed class ClientConnectionStream : Stream
{
    private readonly Connection _connection;
    private readonly Stream _inner;

    public ClientConnectionStream(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _inner = connection.AsStream();
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => _inner.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The in-memory connection's disposal completes synchronously (it only completes
            // pipe ends), so blocking on it here cannot deadlock.
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
