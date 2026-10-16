using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Internal;

// Removes the union of deleted extents minus the union of retained extents, without reading data.
internal sealed class HeifDataCompaction
{
    private readonly List<(long Start, long End)> _removed;
    private readonly long[] _removedBefore;
    internal BinarySegment Original { get; }
    internal HeifSegmentStream Stream { get; }
    internal BinarySegment Payload { get; }

    private HeifDataCompaction(BinarySegment original, List<(long Start, long End)> removed)
    {
        Original = original;
        _removed = removed;
        _removedBefore = new long[removed.Count];
        var kept = new List<BinarySegment>(removed.Count + 1);
        long position = 0, total = 0;
        for (var i = 0; i < removed.Count; i++)
        {
            var range = removed[i];
            if (position < range.Start) { kept.Add(original.Slice(position, range.Start - position)); }
            position = range.End;
            _removedBefore[i] = total;
            total += range.End - range.Start;
        }
        if (position < original.Length) { kept.Add(original.Slice(position, original.Length - position)); }
        Stream = new HeifSegmentStream(kept);
        Payload = BinarySegment.Create(Stream, 0, Stream.Length);
    }

    internal static HeifDataCompaction? Create(BinarySegment payload, List<HeifDataRange> deleted, List<HeifDataRange> retained)
    {
        var candidates = Merge(deleted, payload.Length);
        var protectedRanges = Merge(retained, payload.Length);
        var removed = new List<(long Start, long End)>();
        var keepIndex = 0;
        foreach (var candidate in candidates)
        {
            var position = candidate.Start;
            while (keepIndex < protectedRanges.Count && protectedRanges[keepIndex].End <= position) { keepIndex++; }
            for (var i = keepIndex; i < protectedRanges.Count && protectedRanges[i].Start < candidate.End; i++)
            {
                var keep = protectedRanges[i];
                if (position < keep.Start) { removed.Add((position, keep.Start)); }
                position = Math.Max(position, keep.End);
            }
            if (position < candidate.End) { removed.Add((position, candidate.End)); }
        }
        return removed.Count == 0 ? null : new HeifDataCompaction(payload, removed);
    }

    private static List<(long Start, long End)> Merge(List<HeifDataRange> ranges, long length)
    {
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var result = new List<(long Start, long End)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.Start < 0 || range.Length < 0 || range.Start > length || range.Length > length - range.Start)
            {
                throw new ContentFormatException(ErrorMessages.InvalidExtent, ContentFormatErrorCode.InvalidStructure, range.Box.Offset);
            }
            if (range.Length == 0) { continue; }
            var end = range.Start + range.Length;
            if (result.Count != 0 && range.Start <= result[^1].End)
            {
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, end));
            }
            else { result.Add((range.Start, end)); }
        }
        return result;
    }

    internal long Map(long offset, bool end)
    {
        if (offset < 0 || offset > Original.Length) { return -1; }
        var low = 0;
        var high = _removed.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_removed[middle].Start <= offset) { low = middle + 1; }
            else { high = middle; }
        }
        if (low == 0) { return offset; }
        var index = low - 1;
        var range = _removed[index];
        if (offset < range.End)
        {
            return end ? range.Start - _removedBefore[index] : -1;
        }
        return offset - _removedBefore[index] - (range.End - range.Start);
    }
}
