using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Tests;

/// <summary>
/// A memory-backed stream that records disposal and can present itself as non-seekable, used to verify
/// content ownership and single-use semantics.
/// </summary>
internal sealed class TrackingStream : Stream
{
    private readonly MemoryStream _inner;
    private readonly bool _seekable;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackingStream"/> class.
    /// </summary>
    /// <param name="data">The read-only bytes the stream exposes.</param>
    /// <param name="seekable">Whether the stream reports itself as seekable and allows seeking.</param>
    public TrackingStream(byte[] data, bool seekable = true)
    {
        _inner = new(data, writable: false);
        _seekable = seekable;
    }

    public bool Disposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => _seekable;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set
        {
            if (!_seekable)
            {
                throw new NotSupportedException();
            }

            _inner.Position = value;
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) =>
        _seekable ? _inner.Seek(offset, origin) : throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disposed = true;
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
