using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

[global::System.Diagnostics.DebuggerDisplay("BmffLayoutEntry")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal readonly record struct BmffLayoutEntry(long Offset, int HeaderSize, long PayloadSize)
{
    internal long PayloadOffset => Offset + HeaderSize;
    internal long Size => HeaderSize + PayloadSize;

    private sealed class DebugView
    {
        private readonly BmffLayoutEntry _target;

        public DebugView(BmffLayoutEntry value)
        {
            _target = value;
        }

        public long Offset => _target.Offset;
        public int HeaderSize => _target.HeaderSize;
        public long PayloadSize => _target.PayloadSize;
    }
}

[global::System.Diagnostics.DebuggerDisplay("BmffLayout")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffLayout
{
    internal Func<BmffBox, long, bool, long>? PayloadOffsetMap { get; set; }
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

            if (!target.Entries.TryGetValue(pair.Key, out var destination))
            {
                throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, pair.Value.Offset);
            }

            return (ulong)destination.PayloadOffset + MapPayload(pair.Key, address - start, target);
        }
        return address;
    }

    internal ulong MapAddress(ulong address, BmffLayout target, bool end = false)
    {
        // Prefer the innermost box: auxiliary data can reside in an opaque senc inside moof.
        BmffBox? selected = null;
        BmffLayoutEntry source = default;
        foreach (var pair in Entries)
        {
            var start = (ulong)pair.Value.Offset;
            var limit = start + (ulong)pair.Value.Size;
            if ((end ? address > start && address <= limit : address >= start && address < limit) &&
                (selected is null || pair.Value.Size < source.Size))
            {
                selected = pair.Key;
                source = pair.Value;
            }
        }
        if (selected is null || !target.Entries.TryGetValue(selected, out var destination))
        {
            throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure,
                address <= long.MaxValue ? (long)address : null);
        }
        if (end && address == (ulong)(source.Offset + source.Size))
        {
            return (ulong)(destination.Offset + destination.Size);
        }
        if (address >= (ulong)source.PayloadOffset)
        {
            var relative = MapPayload(selected, address - (ulong)source.PayloadOffset, target, end);
            return (ulong)destination.PayloadOffset + relative;
        }
        var headerRelative = address - (ulong)source.Offset;
        if (headerRelative >= (ulong)destination.HeaderSize)
        {
            throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, source.Offset);
        }
        return (ulong)destination.Offset + headerRelative;
    }

    internal ulong MapPayload(BmffBox box, ulong offset, BmffLayout target, bool end = false)
    {
        if (offset > long.MaxValue || !target.Entries.TryGetValue(box, out var destination))
        {
            throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, box.Offset);
        }
        var mapped = PayloadOffsetMap?.Invoke(box, (long)offset, end) ?? (long)offset;
        if (mapped < 0 || mapped > destination.PayloadSize || (!end && mapped == destination.PayloadSize))
        {
            throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, box.Offset);
        }
        return (ulong)mapped;
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

    private sealed class DebugView
    {
        public DebugView(BmffLayout value)
        {
        }
    }
}
