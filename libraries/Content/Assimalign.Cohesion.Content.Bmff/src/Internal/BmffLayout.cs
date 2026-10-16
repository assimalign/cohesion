using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

internal readonly record struct BmffLayoutEntry(long Offset, int HeaderSize, long PayloadSize)
{
    internal long PayloadOffset => Offset + HeaderSize;
    internal long Size => HeaderSize + PayloadSize;
}

internal sealed class BmffLayout
{
    internal Dictionary<BmffBox, BmffLayoutEntry> Entries { get; } = new(ReferenceEqualityComparer.Instance);

    internal static BmffLayout Original(IList<BmffBox> boxes)
    {
        var result = new BmffLayout();
        foreach (var box in boxes)
        {
            result.AddOriginal(box);
        }

        return result;
    }

    private void AddOriginal(BmffBox box)
    {
        var header = box.Header;
        Entries.Add(box, new BmffLayoutEntry(header.Offset, header.HeaderSize, header.TotalSize!.Value - header.HeaderSize));
        if (box is BmffBoxComposite composite)
        {
            foreach (var child in composite.Children)
            {
                AddOriginal(child);
            }
        }
    }

    internal static BmffLayout Measure(IList<BmffBox> boxes, long offset, int maxDepth)
    {
        var layout = new BmffLayout();
        foreach (var box in boxes)
        {
            var plan = BmffWritePlan.Create(box, 0, maxDepth, true);
            if (plan.Size > long.MaxValue - offset)
            {
                throw new ContentFormatException(ErrorMessages.SizeLimit, ContentFormatErrorCode.LimitExceeded, offset);
            }

            plan.AddLayout(layout, offset);
            offset += plan.Size;
        }
        return layout;
    }

    internal bool SamePositions(BmffLayout other)
    {
        if (Entries.Count != other.Entries.Count)
        {
            return false;
        }

        foreach (var pair in Entries)
        {
            if (!other.Entries.TryGetValue(pair.Key, out var value) || pair.Value != value)
            {
                return false;
            }
        }

        return true;
    }

    internal ulong MapData(ulong address, BmffLayout target)
    {
        foreach (var pair in Entries)
        {
            if (pair.Key is not MediaDataBox)
            {
                continue;
            }

            var start = (ulong)pair.Value.PayloadOffset;
            if (address < start || address - start >= (ulong)pair.Value.PayloadSize)
            {
                continue;
            }

            if (!target.Entries.TryGetValue(pair.Key, out var destination) || address - start >= (ulong)destination.PayloadSize)
            {
                throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, pair.Value.Offset);
            }

            return (ulong)destination.PayloadOffset + address - start;
        }
        return address;
    }

    internal ulong MapMoof(ulong address, BmffLayout target)
    {
        foreach (var pair in Entries)
        {
            if (pair.Key is not MovieFragmentBox || (ulong)pair.Value.Offset != address)
            {
                continue;
            }

            if (!target.Entries.TryGetValue(pair.Key, out var destination))
            {
                throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, pair.Value.Offset);
            }

            return (ulong)destination.Offset;
        }
        return address;
    }

    internal ulong MapBase(ulong address, BmffLayout target)
    {
        var data = MapData(address, target);
        return data == address ? MapMoof(address, target) : data;
    }
}
