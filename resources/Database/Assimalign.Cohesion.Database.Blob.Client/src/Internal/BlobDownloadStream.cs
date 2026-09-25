using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client.Internal;

// The shared stream owns buffering, cancellation and the exchange lifetime. This
// facade preserves the Blob API's exception type after shared health processing.
internal sealed class BlobDownloadStream : Stream
{
    private readonly Stream _stream;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobDownloadStream"/> class.
    /// </summary>
    /// <param name="stream">The shared download stream whose client exceptions are translated to Blob exceptions.</param>
    public BlobDownloadStream(Stream stream)
    {
        _stream = stream;
    }

    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _stream.Read(buffer, offset, count);
        }
        catch (DatabaseClientException exception)
        {
            throw new BlobClientException(exception.Code, exception.Message, exception);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseClientException exception)
        {
            throw new BlobClientException(exception.Code, exception.Message, exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
