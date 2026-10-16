using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal sealed class UntouchableStream : Stream
{
    public override bool CanRead => throw new InvalidOperationException();
    public override bool CanSeek => throw new InvalidOperationException();
    public override bool CanWrite => throw new InvalidOperationException();
    public override long Length => throw new InvalidOperationException();
    public override long Position { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException();
    public override int Read(Span<byte> buffer) => throw new InvalidOperationException();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException();
    public override void Flush() => throw new InvalidOperationException();
    public override void SetLength(long value) => throw new InvalidOperationException();
    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
}
