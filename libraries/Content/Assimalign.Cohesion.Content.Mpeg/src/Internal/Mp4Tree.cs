using System.Collections.Generic;

using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal static class Mp4Tree
{
    internal static T? Child<T>(IList<BmffBox> children) where T : BmffBox
    {
        T? result = null;
        foreach (var child in children)
        {
            if (child is T match)
            {
                if (result is not null)
                {
                    throw new ContentFormatException(ErrorMessages.DuplicateBox, ContentFormatErrorCode.InvalidStructure, child.Offset);
                }
                result = match;
            }
        }
        return result;
    }

    internal static T Required<T>(BmffBoxComposite parent) where T : BmffBox =>
        Child<T>(parent.Children) ?? throw new ContentFormatException(ErrorMessages.RequiredBoxMissing, ContentFormatErrorCode.InvalidStructure, parent.Offset);

    internal static bool Contains(IList<BmffBox> boxes, uint type)
    {
        foreach (var box in boxes)
        {
            if ((uint)box.BoxType == type) { return true; }
            if (box is BmffBoxComposite composite && Contains(composite.Children, type)) { return true; }
        }
        return false;
    }

    internal static bool InMedia(IList<BmffBox> boxes, long start, long length, long lower = 0, long upper = long.MaxValue)
    {
        if (start < lower || start < 0 || length < 0 || start > upper || length > upper - start) { return false; }
        foreach (var box in boxes)
        {
            if (box is MediaDataBox media)
            {
                var payloadStart = media.Offset + media.Header.HeaderSize;
                if (start >= payloadStart && start - payloadStart <= media.Payload.Length &&
                    length <= media.Payload.Length - (start - payloadStart)) { return true; }
            }
        }
        return false;
    }

    internal static BmffBox? UnhandledOffset(IList<BmffBox> boxes)
    {
        foreach (var box in boxes)
        {
            if ((uint)box.BoxType is 0x73696478 or 0x7361696f) { return box; } // sidx / saio
            if (box is BmffBoxComposite composite)
            {
                var found = UnhandledOffset(composite.Children);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    internal static bool HasDependencies(IList<BmffBox> boxes) =>
        Contains(boxes, 0x73677064) || Contains(boxes, 0x73626770) ||
        Contains(boxes, 0x7361696f) || Contains(boxes, 0x7361697a) || Contains(boxes, 0x73656e63);
}
