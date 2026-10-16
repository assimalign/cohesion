using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

// BMFF exposes lazy payload capture on its opaque media node, but not on BmffStream.
// Reuse that public behavior; do not retain a disposed payload window or rented leaf buffer.
internal static class Mp4OpaquePayload
{
    internal static BinarySegment Read(BmffStream stream)
    {
        var opaque = new MediaDataBox(stream.Offset, stream.Length);
        opaque.Read(stream);
        stream.Position = stream.Length;
        return opaque.Payload;
    }
}
