namespace Assimalign.Cohesion.Content.Bmff.Internal;

internal static class BmffInspection
{
    // Never ask GetPayloadSize: profile implementations may parse or materialize data there.
    internal static long? GetLazyLength(BmffBox box) => box switch
    {
        BmffBoxLazyLeaf lazy => lazy.Payload.Length,
        UnknownBox unknown => unknown.Payload.Length,
        MediaDataBox media => media.Payload.Length,
        FreeSpaceBox free => free.Payload.Length,
        SkipBox skip => skip.Payload.Length,
        _ => null
    };
}
