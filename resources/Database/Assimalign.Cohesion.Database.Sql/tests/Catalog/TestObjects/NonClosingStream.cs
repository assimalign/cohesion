using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>
/// Lets tests capture bytes after the storage disposes its streams: disposal
/// deliberately keeps the wrapped <see cref="MemoryStream"/> alive.
/// </summary>
internal sealed class NonClosingStream : Stream
{
    private readonly MemoryStream _inner;

    public NonClosingStream(MemoryStream inner) => _inner = inner;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        // deliberately keep the inner stream alive
    }
}
