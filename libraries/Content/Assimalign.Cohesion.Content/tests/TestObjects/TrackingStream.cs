using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Tests;

/// <summary>
/// A memory-backed stream that records disposal and can present itself as non-seekable, used to verify
/// content ownership and single-use semantics.
/// </summary>
internal sealed class TrackingStream(byte[] data, bool seekable = true) : Stream
{
    private readonly MemoryStream _inner = new(data, writable: false);

    public bool Disposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => seekable;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set
        {
            if (!seekable)
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
        seekable ? _inner.Seek(offset, origin) : throw new NotSupportedException();

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
