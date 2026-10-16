using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal interface IMp4OpaqueBox
{
    BinarySegment Data { get; }
}
