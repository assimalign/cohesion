using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// Each closure represents a table/box, never an individual sample. Original arrays are retained
// until the layout converges, so retries do not accumulate deltas and failure can roll back.
internal sealed class BmffOffsetRelocator
{
    private readonly BmffLayout _original;
    private readonly BmffBoxRegistry _registry;
    private readonly List<Action<BmffLayout>> _updates = new();
    private readonly List<Action> _undo = new();

    private BmffOffsetRelocator(BmffLayout original, BmffBoxRegistry registry) { _original = original; _registry = registry; }

    internal static BmffLayout Relocate(IList<BmffBox> boxes, BmffLayout original, long outputOffset, int maxDepth, BmffBoxRegistry registry)
    {
        var current = BmffLayout.Measure(boxes, outputOffset, maxDepth);
        if (current.SamePositions(original))
        {
            return current;
        }

        var relocator = new BmffOffsetRelocator(original, registry);
        try
        {
            relocator.Collect(boxes, null, null, true, null);
            for (var pass = 0; pass <= relocator._updates.Count * 2 + 8; pass++)
            {
                foreach (var update in relocator._updates)
                {
                    update(current);
                }

                var next = BmffLayout.Measure(boxes, outputOffset, maxDepth);
                if (current.SamePositions(next))
                {
                    return next;
                }

                current = next;
            }
            throw new ContentFormatException(ErrorMessages.RelocationConvergence, ContentFormatErrorCode.InvalidStructure);
        }
        catch
        {
            for (var i = relocator._undo.Count - 1; i >= 0; i--)
            {
                relocator._undo[i]();
            }

            throw;
        }
    }

    private void Collect(IList<BmffBox> boxes, MovieFragmentBox? moof, TrackFragmentHeaderBox? tfhd, bool knownBase, MovieFragmentRandomAccessBox? mfra)
    {
        var firstTraf = true;
        for (var index = 0; index < boxes.Count; index++)
        {
            var box = boxes[index];
            if (_registry.Relocations.TryGetValue(box.BoxType, out var capture))
            {
                var rule = capture(box);
                if (rule.Apply is null || rule.Restore is null)
                {
                    throw new InvalidOperationException(ErrorMessages.InvalidFactory);
                }
                _undo.Add(rule.Restore);
                _updates.Add(layout => rule.Apply(new BmffRelocationContext(_original, layout)));
            }
            else
            {
                switch (box)
                {
                    case BmffSegmentIndexBox sidx: AddSegmentIndex(sidx); break;
                    case BmffAuxiliaryOffsetsBox saio: AddAuxiliaryOffsets(saio, moof, tfhd, knownBase); break;
                    case ChunkOffsetBox stco: AddChunk32(boxes, index, stco); break;
                    case ChunkOffset64BitBox co64: AddChunk64(co64); break;
                    case ItemLocationBox iloc: AddItems(iloc); break;
                    case TrackFragmentHeaderBox header: AddBase(header); break;
                    case TrackFragmentRunBox run when moof is not null: AddRun(run, moof, tfhd, knownBase); break;
                    case TrackFragmentRandomAccessBox tfra: AddRandomAccess(tfra); break;
                    case MovieFragmentRandomAccessOffsetBox mfro when mfra is not null: AddMfraSize(mfro, mfra); break;
                    case UnknownBox unknown when unknown.BoxType is BmffBoxType.ChunkOffset or BmffBoxType.ChunkOffset6fBit or BmffBoxType.ItemLocation or BmffBoxType.TrackFragmentHeader or BmffBoxType.TrackFragmentRun or BmffBoxType.TrackFragmentRandomAccess || (uint)unknown.BoxType is 0x73696478 or 0x7361696f:
                        _updates.Add(layout =>
                        {
                            if (!_original.SamePositions(layout))
                            {
                                throw new ContentFormatException(ErrorMessages.RelocationUnsupported, ContentFormatErrorCode.Unsupported, unknown.Offset);
                            }
                        });
                        break;
                }
                }
            if (box is MovieFragmentBox fragment)
            {
                Collect(fragment.Children, fragment, null, true, null);
            }
            else if (box is TrackFragmentBox track)
            {
                TrackFragmentHeaderBox? header = null;
                foreach (var child in track.Children)
                {
                    if (child is TrackFragmentHeaderBox value)
                    {
                        header = value;
                    }
                }

                Collect(track.Children, moof, header, firstTraf || (header is not null && (header.Flags & 0x020000) != 0), mfra);
                firstTraf = false;
            }
            else if (box is BmffBoxComposite composite)
            {
                Collect(composite.Children, moof, tfhd, knownBase, box as MovieFragmentRandomAccessBox ?? mfra);
            }
        }
    }

    private void AddSegmentIndex(BmffSegmentIndexBox box)
    {
        if (!_original.Entries.TryGetValue(box, out var old)) { return; }
        var first = box.FirstOffset;
        var entries = box.Entries;
        var version = box.Version;
        var start = Add((ulong)(old.Offset + old.Size), first, box.Offset);
        _undo.Add(() => { box.FirstOffset = first; box.Entries = entries; box.Version = version; });
        _updates.Add(layout =>
        {
            if (entries.Length == 0) { return; }
            var target = _original.MapAddress(start, layout);
            var output = layout.Entries[box];
            var anchor = (ulong)(output.Offset + output.Size);
            if (target < anchor)
            {
                throw new ContentFormatException(ErrorMessages.RelocationUnsupported, ContentFormatErrorCode.Unsupported, box.Offset);
            }
            box.FirstOffset = target - anchor;
            if (box.FirstOffset > uint.MaxValue) { box.Version = 1; }
            var values = (BmffSegmentIndexBox.Entry[])entries.Clone();
            var position = start;
            var nextTarget = target;
            for (var i = 0; i < entries.Length; i++)
            {
                var size = entries[i].Reference & 0x7fffffffu;
                var end = Add(position, size, box.Offset);
                var mappedStart = _original.MapAddress(position, layout);
                var mappedEnd = _original.MapAddress(end, layout, end: true);
                if (size == 0 || mappedStart != nextTarget || mappedEnd < mappedStart || mappedEnd - mappedStart > 0x7fffffff)
                {
                    throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, box.Offset);
                }
                values[i] = entries[i] with { Reference = (entries[i].Reference & 0x80000000u) | (uint)(mappedEnd - mappedStart) };
                position = end;
                nextTarget = mappedEnd;
            }
            box.Entries = values;
        });
    }

    private void AddAuxiliaryOffsets(BmffAuxiliaryOffsetsBox box, MovieFragmentBox? moof, TrackFragmentHeaderBox? header, bool knownBase)
    {
        var offsets = box.Offsets;
        var version = box.Version;
        var explicitBase = header?.BaseDataOffset;
        var oldBase = moof is null ? 0UL : explicitBase ?? (ulong)_original.Entries[moof].Offset;
        _undo.Add(() => { box.Offsets = offsets; box.Version = version; });
        _updates.Add(layout =>
        {
            if (moof is not null && !knownBase && !explicitBase.HasValue && offsets.Length != 0)
            {
                throw new ContentFormatException(ErrorMessages.RelocationUnsupported, ContentFormatErrorCode.Unsupported, box.Offset);
            }
            var newBase = moof is null ? 0UL : explicitBase.HasValue
                ? _original.MapBase(oldBase, layout) : (ulong)layout.Entries[moof].Offset;
            var values = new ulong[offsets.Length];
            for (var i = 0; i < offsets.Length; i++)
            {
                var target = _original.MapAddress(Add(oldBase, offsets[i], box.Offset), layout);
                if (target < newBase)
                {
                    throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, box.Offset);
                }
                values[i] = target - newBase;
                if (values[i] > uint.MaxValue) { box.Version = 1; }
            }
            box.Offsets = values;
        });
    }

    private static ulong Add(ulong left, ulong right, long position)
    {
        if (right > ulong.MaxValue - left)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, position);
        }
        return left + right;
    }

    private void AddChunk32(IList<BmffBox> parent, int index, ChunkOffsetBox box)
    {
        var entries = box.Entries;
        _undo.Add(() => { parent[index] = box; box.Entries = entries; });
        _updates.Add(layout =>
        {
            var moved = new ulong[entries.Length];
            var needs64 = parent[index] is ChunkOffset64BitBox;
            for (var i = 0; i < entries.Length; i++)
            {
                moved[i] = _original.MapData(entries[i], layout);
                needs64 |= moved[i] > uint.MaxValue;
            }
            if (needs64)
            {
                var promoted = parent[index] as ChunkOffset64BitBox ?? new ChunkOffset64BitBox(box.Offset, box.Limit)
                {
                    Version = box.Version, Flags = box.Flags, PreservedTail = box.PreservedTail
                };
                promoted.Entries = moved;
                parent[index] = promoted;
            }
            else
            {
                var values = new uint[entries.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = (uint)moved[i];
                }

                box.Entries = values;
            }
        });
    }

    private void AddChunk64(ChunkOffset64BitBox box)
    {
        var entries = box.Entries;
        _undo.Add(() => box.Entries = entries);
        _updates.Add(layout =>
        {
            var values = new ulong[entries.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = _original.MapData(entries[i], layout);
            }

            box.Entries = values;
        });
    }

    private void AddBase(TrackFragmentHeaderBox box)
    {
        var value = box.BaseDataOffset;
        if (!value.HasValue)
        {
            return;
        }

        _undo.Add(() => box.BaseDataOffset = value);
        _updates.Add(layout => box.BaseDataOffset = _original.MapBase(value.Value, layout));
    }

    private void AddItems(ItemLocationBox box)
    {
        var entries = box.Entries;
        var baseSize = box.BaseOffsetSize;
        var offsetSize = box.OffsetSize;
        _undo.Add(() => { box.Entries = entries; box.BaseOffsetSize = baseSize; box.OffsetSize = offsetSize; });
        _updates.Add(layout =>
        {
            var values = (ItemLocationBox.Entry[])entries.Clone();
            for (var i = 0; i < values.Length; i++)
            {
                var old = entries[i];
                if (old.DataReferenceIndex != 0 || old.ConstructionMethod != 0)
                {
                    continue;
                }

                var newBase = _original.MapData(old.BaseOffset, layout);
                var targets = new ulong[old.Extents.Length];
                for (var j = 0; j < targets.Length; j++)
                {
                    if (old.Extents[j].Offset > ulong.MaxValue - old.BaseOffset)
                    {
                        throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, box.Offset);
                    }

                    targets[j] = _original.MapData(old.BaseOffset + old.Extents[j].Offset, layout);
                    if (targets[j] < newBase)
                    {
                        newBase = 0;
                    }
                }
                values[i].BaseOffset = newBase;
                values[i].Extents = (ItemLocationBox.Extent[])old.Extents.Clone();
                box.BaseOffsetSize = Grow(box.BaseOffsetSize, newBase);
                for (var j = 0; j < targets.Length; j++)
                {
                    values[i].Extents[j].Offset = targets[j] - newBase;
                    box.OffsetSize = Grow(box.OffsetSize, values[i].Extents[j].Offset);
                }
            }
            box.Entries = values;
        });
    }

    private static byte Grow(byte size, ulong value) =>
        value > uint.MaxValue ? (byte)8 : value != 0 && size == 0 ? (byte)4 : size;

    private void AddRun(TrackFragmentRunBox box, MovieFragmentBox moof, TrackFragmentHeaderBox? header, bool knownBase)
    {
        var dataOffset = box.DataOffset;
        if (!dataOffset.HasValue || !_original.Entries.TryGetValue(moof, out var oldMoof))
        {
            return;
        }
        // Capture before any tfhd update. Implicit bases after the first traf need profile sample indexing.
        var explicitBase = header?.BaseDataOffset;
        var oldBase = explicitBase ?? (ulong)oldMoof.Offset;
        _undo.Add(() => box.DataOffset = dataOffset);
        _updates.Add(layout =>
        {
            if (!knownBase && !explicitBase.HasValue)
            {
                if (!_original.SamePositions(layout))
                {
                    throw new ContentFormatException(ErrorMessages.RelocationUnsupported, ContentFormatErrorCode.Unsupported, box.Offset);
                }

                return;
            }
            var target = AddSigned(oldBase, dataOffset.Value, box.Offset);
            var newTarget = _original.MapData(target, layout);
            var newBase = explicitBase.HasValue ? _original.MapBase(oldBase, layout) : (ulong)layout.Entries[moof].Offset;
            var distance = newTarget >= newBase ? newTarget - newBase : newBase - newTarget;
            if (distance > (newTarget >= newBase ? int.MaxValue : 2147483648UL))
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, box.Offset);
            }

            box.DataOffset = newTarget >= newBase ? (int)distance : (int)-(long)distance;
        });
    }

    private static ulong AddSigned(ulong value, int delta, long position)
    {
        if ((delta < 0 && value < (ulong)-(long)delta) || (delta >= 0 && value > ulong.MaxValue - (uint)delta))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, position);
        }

        return delta < 0 ? value - (ulong)-(long)delta : value + (uint)delta;
    }

    private void AddRandomAccess(TrackFragmentRandomAccessBox box)
    {
        var entries = box.Entries;
        var version = box.Version;
        _undo.Add(() => { box.Entries = entries; box.Version = version; });
        _updates.Add(layout =>
        {
            var values = (TrackFragmentRandomAccessBox.Entry[])entries.Clone();
            for (var i = 0; i < values.Length; i++)
            {
                values[i].MoofOffset = _original.MapMoof(entries[i].MoofOffset, layout);
                if (values[i].MoofOffset > uint.MaxValue)
                {
                    box.Version = 1;
                }
            }
            box.Entries = values;
        });
    }

    private void AddMfraSize(MovieFragmentRandomAccessOffsetBox box, MovieFragmentRandomAccessBox parent)
    {
        var value = box.MfraSize;
        _undo.Add(() => box.MfraSize = value);
        _updates.Add(layout =>
        {
            var size = layout.Entries[parent].Size;
            // An unchanged parsed mfro (even one from a permissive producer) remains byte-identical.
            if (_original.Entries.TryGetValue(parent, out var original) && original.Size == size)
            {
                return;
            }

            if (size > uint.MaxValue)
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, box.Offset);
            }

            box.MfraSize = (uint)size;
        });
    }
}
