using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// Adapts legacy field-at-a-time box readers without changing their model or stream lifecycle.
internal static class BmffBinary
{
    internal static BinaryContentReader ReadField(Stream stream, Span<byte> buffer)
    {
        var offset = stream.Position;
        stream.ReadContentExactly(buffer);
        return new BinaryContentReader(buffer, offset);
    }
}
