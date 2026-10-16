using System;
using System.Buffers.Binary;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

// Original extension fixture: an opaque zzzz leaf containing one absolute UInt64 offset.
internal static class BmffRelocationProfile
{
    internal static (Action<IBmffRelocationContext> Apply, Action Restore) Capture(BmffBox box)
    {
        var leaf = (UnknownBox)box;
        var payload = leaf.Payload;
        Span<byte> original = stackalloc byte[8];
        using (var stream = payload.OpenRead()) { stream.ReadContentExactly(original); }
        var offset = BinaryPrimitives.ReadUInt64BigEndian(original);
        return (context =>
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, context.MapOffset(offset));
            leaf.Payload = BinarySegment.Create(bytes);
        }, () => leaf.Payload = payload);
    }
}
