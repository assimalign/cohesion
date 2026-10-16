// Linked-source precedent: .claude/rules/testing.md. Downstream Content tests may use:
// <Compile Include="$(CohesionRepositoryDirectory)libraries\Content\Assimalign.Cohesion.Content.Binary\tests\Shared\*.cs" Link="Shared\Binary\%(Filename)%(Extension)" />
// Deviates from the consuming RootNamespace rule for linked test support; instances never cross assemblies.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal sealed class ChunkedReadStream : Stream
{
    private readonly Stream _source;
    private readonly int _maxRead;
    private readonly bool _leaveOpen;

    internal ChunkedReadStream(Stream source, int maxRead, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRead);
        _source = source;
        _maxRead = maxRead;
        _leaveOpen = leaveOpen;
    }

    internal int ReadCalls { get; private set; }
    internal int AsyncReadCalls { get; private set; }
    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => _source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position { get => _source.Position; set => _source.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        return _source.Read(buffer[..Math.Min(buffer.Length, _maxRead)]);
    }
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        AsyncReadCalls++;
        return _source.ReadAsync(buffer[..Math.Min(buffer.Length, _maxRead)], cancellationToken);
    }
    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _source.Dispose();
        }
        base.Dispose(disposing);
    }
}
