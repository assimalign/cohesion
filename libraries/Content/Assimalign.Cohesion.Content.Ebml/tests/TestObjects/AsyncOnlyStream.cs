using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

// Rejects synchronous I/O while retaining seeking, proving the document adapters use real async I/O.
internal sealed class AsyncOnlyStream : Stream
{
    private readonly Stream _source;
    private readonly CancellationTokenSource? _cancel;
    private readonly int _cancelAfterReads;
    private int _reads;
    internal AsyncOnlyStream(Stream source, CancellationTokenSource? cancel = null, int cancelAfterReads = int.MaxValue)
    {
        _source = source; _cancel = cancel; _cancelAfterReads = cancelAfterReads;
    }
    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => _source.CanSeek;
    public override bool CanWrite => _source.CanWrite;
    public override long Length => _source.Length;
    public override long Position { get => _source.Position; set => _source.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException();
    public override int Read(Span<byte> buffer) => throw new InvalidOperationException();
    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var result = await _source.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        if (++_reads == _cancelAfterReads)
        {
            _cancel?.Cancel();
        }
        return result;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _source.WriteAsync(buffer, cancellationToken);
    public override void Flush() => _source.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    public override void SetLength(long value) => _source.SetLength(value);
}
