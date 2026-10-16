// Linked-source precedent: .claude/rules/testing.md. Downstream Content tests may use:
// <Compile Include="$(CohesionRepositoryDirectory)libraries\Content\Assimalign.Cohesion.Content.Binary\tests\Shared\*.cs" Link="Shared\Binary\%(Filename)%(Extension)" />
// Deviates from the consuming RootNamespace rule for linked test support; instances never cross assemblies.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal sealed class ReadLimitStream : Stream
{
    private readonly Stream _source;
    private readonly long _limit;
    private long _consumed;

    internal ReadLimitStream(Stream source, long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _source = source;
        _limit = limit;
    }

    internal int ReadCalls { get; private set; }
    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => _source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position { get => _source.Position; set => _source.Position = value; }

    private void Check(int count)
    {
        var position = _source.CanSeek ? _source.Position : _consumed;
        if (count > 0 && (position >= _limit || count > _limit - position))
        {
            throw new InvalidOperationException("A read request crossed the configured test boundary.");
        }
        ReadCalls++;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        Check(buffer.Length);
        var read = _source.Read(buffer);
        _consumed += read;
        return read;
    }
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(buffer.Length);
        var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _consumed += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }
        base.Dispose(disposing);
    }
}
