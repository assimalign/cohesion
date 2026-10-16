using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Heif.Tests;

// Ends the save immediately after planning, before a sparse fixture's image data could be read.
internal sealed class HeifFailingOutput : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new IOException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException();
}
