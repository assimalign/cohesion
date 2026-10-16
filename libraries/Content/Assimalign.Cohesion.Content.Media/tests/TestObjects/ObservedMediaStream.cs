using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Media.Tests;

// Opaque bytes authored in code. Read counts detect accidental eager codec-private materialization.
internal sealed class ObservedMediaStream : MemoryStream
{
    internal ObservedMediaStream() : base(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, writable: false) { }

    internal int Reads { get; private set; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        Reads++;
        return base.Read(buffer, offset, count);
    }
    public override int Read(Span<byte> buffer)
    {
        Reads++;
        return base.Read(buffer);
    }
    public override int ReadByte()
    {
        Reads++;
        return base.ReadByte();
    }
}
