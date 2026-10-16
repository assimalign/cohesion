using System;
using System.Globalization;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

internal static class BmffInspection
{
    internal static string GetDebuggerDisplay(BmffBox box)
    {
        var size = box.Header.TotalSize ?? (box.Header.HeaderSize == 0 && box.Limit != 0 ? box.Limit : (long?)null);
        var sizeText = size is >= 1024L * 1024 * 1024
            ? (size.Value / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB"
            : size?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";
        var shape = box is BmffBoxComposite composite
            ? FormattableString.Invariant($" [{composite.Children.Count} children]")
            : GetLazyLength(box).HasValue ? " (lazy)" : "";
        var unloaded = box.Header.HeaderSize == 0 ? " [unloaded]" : "";
        var preserved = box is UnknownBox ? " [preserved]" : "";
        return FormattableString.Invariant($"{box.BoxType.ToFourCC()} @0x{box.Offset:X} size {sizeText}{shape}{unloaded}{preserved}");
    }

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
