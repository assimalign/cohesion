// Linked-source precedent: .claude/rules/testing.md. Downstream Content tests may use:
// <Compile Include="$(CohesionRepositoryDirectory)libraries\Content\Assimalign.Cohesion.Content.Binary\tests\Shared\*.cs" Link="Shared\Binary\%(Filename)%(Extension)" />
// Deviates from the consuming RootNamespace rule for linked test support; instances never cross assemblies.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal sealed class NonSeekableStream : Stream
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;

    internal NonSeekableStream(Stream source, bool leaveOpen = false)
    {
        _source = source;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _source.Read(buffer);
    public override int ReadByte() => _source.ReadByte();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) =>
        _source.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _source.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
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
