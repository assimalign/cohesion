using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

public sealed partial class HeifDocument
{
    private readonly Dictionary<uint, List<HeifDataRange>> _itemRanges = new();
    private readonly List<HeifDataRange> _deletedRanges = new();

    private void CheckRemovalRanges(HeifItem item)
    {
        // Unresolved/external locations cannot establish either what to erase or what is shared.
        foreach (var entry in _locations!.Entries)
        {
            if (entry.DataReferenceIndex != 0 || entry.ConstructionMethod > 1 ||
                !_itemRanges.TryGetValue(entry.ItemId, out var ranges) || ranges.Count != entry.Extents.Length)
            {
                throw new ContentFormatException(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, _locations.Offset);
            }
        }
        if (!_itemRanges.ContainsKey(item.Id))
        {
            throw new ContentFormatException(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, item.Info.Offset);
        }
    }

    private void CompactDeletedData(Stream output)
    {
        if (_deletedRanges.Count == 0) { return; }
        ArgumentNullException.ThrowIfNull(output);
        if (ReferenceEquals(output, Document.Source)) { throw new InvalidOperationException(ErrorMessages.InvalidEdit); }
        var deleted = new Dictionary<BmffBox, List<HeifDataRange>>();
        var retained = new Dictionary<BmffBox, List<HeifDataRange>>();
        foreach (var range in _deletedRanges) { AddRange(deleted, range); }
        foreach (var ranges in _itemRanges.Values)
        {
            foreach (var range in ranges) { AddRange(retained, range); }
        }
        var plans = new Dictionary<BmffBox, HeifDataCompaction>();
        foreach (var pair in deleted)
        {
            var payload = pair.Key is MediaDataBox media ? media.Payload : ((ItemDataBox)pair.Key).Payload;
            var plan = HeifDataCompaction.Create(payload, pair.Value, retained.GetValueOrDefault(pair.Key) ?? new());
            if (plan is not null) { plans.Add(pair.Key, plan); }
        }
        var outputOffset = output.CanSeek ? output.Position : 0;
        if (plans.Count == 0) { _deletedRanges.Clear(); return; }
        ItemLocationBox.Entry[]? originalEntries = null;
        var offsetSize = _locations!.OffsetSize;
        var lengthSize = _locations.LengthSize;
        try
        {
            // First establish the current layout; item bindings are payload-relative, so survive it.
            Document.RelocateMediaOffsets(outputOffset);
            originalEntries = _locations!.Entries;
            offsetSize = _locations.OffsetSize;
            lengthSize = _locations.LengthSize;
            var normalized = (ItemLocationBox.Entry[])originalEntries.Clone();
            for (var i = 0; i < normalized.Length; i++)
            {
                var entry = originalEntries[i];
                var ranges = _itemRanges[entry.ItemId];
                normalized[i].BaseOffset = 0;
                normalized[i].Extents = (ItemLocationBox.Extent[])entry.Extents.Clone();
                for (var j = 0; j < entry.Extents.Length; j++)
                {
                    if (entry.Extents[j].Offset > ulong.MaxValue - entry.BaseOffset)
                    {
                        throw new ContentFormatException(ErrorMessages.InvalidExtent, ContentFormatErrorCode.InvalidStructure, _locations.Offset);
                    }
                    normalized[i].Extents[j].Offset = entry.BaseOffset + entry.Extents[j].Offset;
                    normalized[i].Extents[j].Length = (ulong)ranges[j].Length;
                    _locations.OffsetSize = GrowWidth(_locations.OffsetSize, normalized[i].Extents[j].Offset);
                    _locations.LengthSize = GrowWidth(_locations.LengthSize, normalized[i].Extents[j].Length);
                }
            }
            _locations.Entries = normalized;
            foreach (var pair in plans) { SetPayload(pair.Key, pair.Value.Payload); }
            Document.RelocateMediaOffsets((box, offset, end) => plans.TryGetValue(box, out var plan) ? plan.Map(offset, end) : offset, outputOffset);
        }
        catch
        {
            if (originalEntries is not null) { _locations!.Entries = originalEntries; }
            _locations.OffsetSize = offsetSize;
            _locations.LengthSize = lengthSize;
            foreach (var pair in plans) { SetPayload(pair.Key, pair.Value.Original); pair.Value.Stream.Dispose(); }
            throw;
        }
        foreach (var ranges in _itemRanges.Values)
        {
            for (var i = 0; i < ranges.Count; i++)
            {
                var range = ranges[i];
                if (plans.TryGetValue(range.Box, out var plan)) { ranges[i] = range with { Start = plan.Map(range.Start, false) }; }
            }
        }
        foreach (var plan in plans.Values) { _ownedStreams.Add(plan.Stream); }
        if (_idat is not null)
        {
            _originalIdat = _idat.Payload;
            _appendedData.Clear();
        }
        _deletedRanges.Clear();
    }

    private static void AddRange(Dictionary<BmffBox, List<HeifDataRange>> map, HeifDataRange range)
    {
        if (!map.TryGetValue(range.Box, out var list)) { list = new(); map.Add(range.Box, list); }
        list.Add(range);
    }
    private static byte GrowWidth(byte width, ulong value) => value > uint.MaxValue ? (byte)8 : value != 0 && width == 0 ? (byte)4 : width;
    private static void SetPayload(BmffBox box, BinarySegment payload)
    {
        if (box is MediaDataBox media) { media.Payload = payload; }
        else { ((ItemDataBox)box).Payload = payload; }
    }
}
