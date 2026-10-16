using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

public sealed partial class HeifDocument
{
    private readonly HashSet<uint> _unrelocatable = new();

    private T? Find<T>(IList<BmffBox> boxes) where T : BmffBox
    {
        T? found = null;
        foreach (var box in boxes)
        {
            if (box is not T value)
            {
                continue;
            }

            if (found is not null)
            {
                Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, box.Offset);
            }
            else
            {
                found = value;
            }
        }
        return found;
    }

    private void ReadItems()
    {
        ReadBrands();
        _meta = Find<MetaBox>(Document.Boxes);
        if (_meta is null) { Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, 0); return; }
        _info = Find<ItemInfoBox>(_meta.Children);
        _locations = Find<ItemLocationBox>(_meta.Children);
        _properties = Find<ItemPropertiesBox>(_meta.Children);
        _references = Find<ItemReferenceBox>(_meta.Children);
        _idat = Find<ItemDataBox>(_meta.Children);
        _originalIdat = _idat?.Payload ?? default;
        if (_properties is not null)
        {
            _propertyContainer = Find<ItemPropertyContainerBox>(_properties.Children);
        }

        if (_info is null || _locations is null)
        { Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, _meta.Offset); return; }
        if (_info.Children.Count > _options.MaxItemCount)
        {
            Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, _info.Offset);
        }

        foreach (var box in _info.Children)
        {
            if (box is not ItemInfoEntryBox info)
            { Report(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, box.Offset); continue; }
            if (info.ItemId == 0 || _byId.ContainsKey(info.ItemId))
            { Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, info.Offset); continue; }
            var item = new HeifItem(info);
            _items.Add(item);
            _byId.Add(item.Id, item);
            if (item.ProtectionIndex != 0)
            {
                Report(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, info.Offset, item);
            }

            if (!item.IsImage && item.Type.Value != 0x45786966 && !IsXmp(item))
            {
                Report(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, info.Offset, item);
            }
        }
        ResolveLocations();
        ReadProperties();
        ReadReferences();
        var primary = Find<PrimaryItemReferenceBox>(_meta.Children);
        PrimaryItem = primary is null ? null : GetItem(primary.ItemId);
        if (PrimaryItem is null || PrimaryItem.IsHidden || !PrimaryItem.IsImage)
        {
            Report(ErrorMessages.MissingPrimary, ContentFormatErrorCode.InvalidStructure, primary?.Offset ?? _meta.Offset, PrimaryItem);
        }

        foreach (var item in _items)
        {
            ReadDerived(item);
        }

        ValidateDerivationGraph();
    }

    private void ReadBrands()
    {
        var fileType = Find<FileTypeBox>(Document.Boxes);
        if (fileType is null) { Report(ErrorMessages.InvalidBrand, ContentFormatErrorCode.InvalidStructure, 0); return; }
        var still = false;
        var sequence = false;
        void Examine(uint brand)
        {
            still |= brand is 0x6d696631 or 0x68656963 or 0x68656978 or 0x6865696d or 0x68656973 or 0x61766966;
            sequence |= brand is 0x6d736631 or 0x61766973 or 0x6176696f;
            IsAvif |= brand is 0x61766966 or 0x61766973 or 0x6176696f;
        }
        Examine(fileType.MajorBrand);
        foreach (var brand in fileType.CompatibleBrands)
        {
            Examine(brand);
        }

        if (!still)
        {
            Report(ErrorMessages.InvalidBrand, sequence ? ContentFormatErrorCode.Unsupported : ContentFormatErrorCode.InvalidStructure, fileType.Offset);
        }
        // Mixed files can expose their still items while explicitly reporting unhandled tracks.
        if (sequence && Find<MovieBox>(Document.Boxes) is not null)
        {
            Report(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, fileType.Offset);
        }
    }

    private void ResolveLocations()
    {
        if (_locations is null)
        {
            return;
        }

        var seen = new HashSet<uint>();
        var totalExtents = 0;
        if (_locations.Entries.Length > _options.MaxItemCount)
        {
            Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, _locations.Offset);
        }

        foreach (var location in _locations.Entries)
        {
            if (location.Extents.Length > _options.MaxExtentCount - totalExtents)
            {
                Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, _locations.Offset);
            }

            totalExtents += location.Extents.Length;
            var item = GetItem(location.ItemId);
            if (item is null || !seen.Add(location.ItemId))
            { Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, _locations.Offset, item); continue; }
            if (location.DataReferenceIndex != 0 || location.ConstructionMethod > 1)
            { Report(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, _locations.Offset, item); continue; }
            if ((location.ConstructionMethod == 0 && Document.Source is null) || (location.ConstructionMethod == 1 && _idat is null))
            { Report(ErrorMessages.InvalidExtent, ContentFormatErrorCode.InvalidStructure, _locations.Offset, item); continue; }
            var available = location.ConstructionMethod == 0 ? Document.Source!.Length : _idat!.Payload.Length;
            var segments = new List<BinarySegment>(location.Extents.Length);
            var positions = new List<long>(location.Extents.Length);
            foreach (var extent in location.Extents)
            {
                // Compare by subtraction before adding untrusted 64-bit fields.
                if (location.BaseOffset > (ulong)available || extent.Offset > (ulong)available - location.BaseOffset)
                { Report(ErrorMessages.InvalidExtent, ContentFormatErrorCode.InvalidStructure, _locations.Offset, item); break; }
                var start = (long)(location.BaseOffset + extent.Offset);
                var length = extent.Length == 0 ? available - start : extent.Length <= (ulong)(available - start) ? (long)extent.Length : -1;
                if (length < 0)
                { Report(ErrorMessages.InvalidExtent, ContentFormatErrorCode.InvalidStructure, _locations.Offset, item); break; }
                if (location.ConstructionMethod == 0)
                {
                    segments.Add(BinarySegment.Create(Document.Source!, start, length));
                    positions.Add(start);
                    var contained = false;
                    foreach (var box in Document.Boxes)
                    {
                        if (box is not MediaDataBox media)
                        {
                            continue;
                        }

                        var dataStart = media.Offset + media.Header.HeaderSize;
                        if (start >= dataStart && start - dataStart <= media.Payload.Length && length <= media.Payload.Length - (start - dataStart))
                        {
                            contained = true;
                        }
                    }
                    if (!contained)
                    {
                        _unrelocatable.Add(item.Id);
                    }
                }
                else
                {
                    segments.Add(_idat!.Payload.Slice(start, length));
                    positions.Add(_idat.Offset + _idat.Header.HeaderSize + start);
                }
            }
            // Invalid extents must never leave a partial payload looking usable.
            if (segments.Count == location.Extents.Length)
            { item.Data = segments.AsReadOnly(); item.DataPositions = positions.ToArray(); }
        }
        foreach (var item in _items)
        {
            if (!seen.Contains(item.Id))
            {
                Report(ErrorMessages.InvalidExtent, ContentFormatErrorCode.InvalidStructure, item.Info.Offset, item);
            }
        }
    }

    private void ReadProperties()
    {
        var count = _propertyContainer?.Children.Count ?? 0;
        if (count > _options.MaxPropertyCount)
        {
            Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, _propertyContainer!.Offset);
        }

        if (_properties is null || _propertyContainer is null)
        { Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, _meta!.Offset); return; }
        var associations = 0L;
        foreach (var box in _properties.Children)
        {
            if (box is not ItemPropertyAssociationBox ipma)
            {
                continue;
            }

            foreach (var entry in ipma.Entries)
            {
                var item = GetItem(entry.ItemId);
                if (item is null) { Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, box.Offset); continue; }
                associations += entry.Associations.Length;
                if (associations * 16 > _options.MaxBufferedPayloadSize)
                {
                    Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, box.Offset);
                }

                foreach (var association in entry.Associations)
                {
                    var property = association.PropertyIndex == 0 || association.PropertyIndex > count ? null : _propertyContainer.Children[association.PropertyIndex - 1];
                    item.PropertyList.Add(new HeifItem.Property(association.PropertyIndex, association.Essential, property));
                    if (association.PropertyIndex > count)
                    {
                        Report(ErrorMessages.MissingProperty, ContentFormatErrorCode.InvalidStructure, box.Offset, item);
                    }
                    else if (association.Essential && !Understood(property))
                    {
                        Report(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, property?.Offset ?? box.Offset, item);
                    }
                }
            }
        }
        foreach (var item in _items)
        {
            if (!item.IsImage)
            {
                continue;
            }

            var spatial = false;
            var configuration = false;
            foreach (var property in item.PropertyList)
            {
                spatial |= property.Box is ImageSpatialExtentsBox;
                configuration |= item.Type.Value == 0x61763031 ? property.Box is Av1ConfigurationBox : property.Box is HevcConfigurationBox;
            }
            if (!spatial || (item.Type.Value is 0x61763031 or 0x68766331 or 0x68657631 && !configuration))
            {
                Report(ErrorMessages.MissingProperty, ContentFormatErrorCode.InvalidStructure, item.Info.Offset, item);
            }
        }
    }

    private static bool Understood(BmffBox? box) => box switch
    {
        ImageSpatialExtentsBox or PixelInformationBox or ImageRotationBox or ImageMirrorBox or CleanApertureBox or AuxiliaryTypeBox or PixelAspectRatioBox or HevcConfigurationBox or Av1ConfigurationBox => true,
        ColourInformationBox colour => colour.ColourType.Value is 0x6e636c78 or 0x70726f66 or 0x72494343,
        _ => false
    };

    private void ReadReferences()
    {
        if (_references is null)
        {
            return;
        }

        foreach (var box in _references.Children)
        {
            var fields = ItemReferenceBox.GetFields(box);
            if (fields is null)
            {
                continue;
            }

            var item = GetItem(fields.FromItemId);
            if (item is null) { Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, box.Offset); continue; }
            item.ReferenceList.Add(new HeifItem.Reference(FourCC.FromUInt32((uint)box.BoxType), fields.ToItemIds));
            foreach (var target in fields.ToItemIds)
            {
                if (!_byId.ContainsKey(target))
                {
                    Report(ErrorMessages.InvalidStructure, ContentFormatErrorCode.InvalidStructure, box.Offset, item);
                }
            }
        }
    }

    private void ReadDerived(HeifItem item)
    {
        if (item.Data.Count == 0 && item.Type.Value != 0x6964656e)
        {
            return;
        }

        try
        {
            if (item.Type.Value == 0x45786966) { ReadExif(item); return; }
            if (item.Type.Value is not (0x67726964 or 0x696f766c or 0x6964656e))
            {
                return;
            }

            var inputs = new List<HeifItem>();
            foreach (var reference in item.ReferenceList)
            {
                if (reference.Type.Value != 0x64696d67)
                {
                    continue;
                }

                foreach (var id in reference.ToItemIds)
                {
                    if (inputs.Count >= _options.MaxGridTileCount)
                    {
                        Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, item.Info.Offset, item);
                    }

                    if (GetItem(id) is not { IsImage: true } input) { Report(ErrorMessages.InvalidGrid, ContentFormatErrorCode.InvalidStructure, item.Info.Offset, item); return; }
                    inputs.Add(input);
                }
            }
            if (item.Type.Value == 0x6964656e)
            {
                if (inputs.Count != 1)
                {
                    Report(ErrorMessages.InvalidGrid, ContentFormatErrorCode.InvalidStructure, item.Info.Offset, item);
                }
                else
                {
                    item.IdentitySource = inputs[0];
                }

                return;
            }
            using var stream = new HeifSegmentStream(item.Data);
            if (stream.Length > _options.MaxBufferedPayloadSize)
            {
                Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, item.Info.Offset, item);
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadContentExactly(bytes);
            var reader = new BinaryContentReader(bytes, item.DataPositions[0]);
            var version = reader.ReadByte();
            var flags = reader.ReadByte();
            HeifFields.Version(version, flags, 0, 1, reader.Position - 2);
            var large = (flags & 1) != 0;
            if (item.Type.Value == 0x67726964)
            {
                var rows = reader.ReadByte() + 1;
                var columns = reader.ReadByte() + 1;
                var width = large ? reader.ReadUInt32BigEndian() : reader.ReadUInt16BigEndian();
                var height = large ? reader.ReadUInt32BigEndian() : reader.ReadUInt16BigEndian();
                if (rows * columns > _options.MaxGridTileCount)
                {
                    Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, reader.Position, item);
                }

                if (rows * columns != inputs.Count || !ValidSize(width, height) || !reader.IsEnd)
                { Report(ErrorMessages.InvalidGrid, ContentFormatErrorCode.InvalidStructure, reader.Position, item); return; }
                item.Grid = new HeifGridImage(rows, columns, width, height, inputs.AsReadOnly());
            }
            else
            {
                var fill = new ushort[4];
                for (var i = 0; i < 4; i++)
                {
                    fill[i] = reader.ReadUInt16BigEndian();
                }

                var width = large ? reader.ReadUInt32BigEndian() : reader.ReadUInt16BigEndian();
                var height = large ? reader.ReadUInt32BigEndian() : reader.ReadUInt16BigEndian();
                if (!ValidSize(width, height) || reader.Remaining != (long)inputs.Count * (large ? 8 : 4))
                { Report(ErrorMessages.InvalidGrid, ContentFormatErrorCode.InvalidStructure, reader.Position, item); return; }
                var layers = new HeifOverlayImage.Layer[inputs.Count];
                for (var i = 0; i < layers.Length; i++)
                {
                    var x = large ? reader.ReadInt32BigEndian() : reader.ReadInt16BigEndian();
                    var y = large ? reader.ReadInt32BigEndian() : reader.ReadInt16BigEndian();
                    layers[i] = new HeifOverlayImage.Layer(inputs[i], x, y);
                }
                item.Overlay = new HeifOverlayImage(width, height, Array.AsReadOnly(fill), Array.AsReadOnly(layers));
            }
        }
        catch (ContentFormatException error) when (!_options.Strict && error.ErrorCode != ContentFormatErrorCode.LimitExceeded)
        { item.IsSupported = false; _diagnostics.Add(error); }
    }

    private bool ValidSize(uint width, uint height) => width != 0 && height != 0 && width <= _options.MaxImageDimension && height <= _options.MaxImageDimension;

    private void ReadExif(HeifItem item)
    {
        using var stream = new HeifSegmentStream(item.Data);
        Span<byte> bytes = stackalloc byte[4];
        if (stream.Length < 4) { Report(ErrorMessages.InvalidExif, ContentFormatErrorCode.Truncated, item.DataPositions[0], item); return; }
        stream.ReadContentExactly(bytes);
        var reader = new BinaryContentReader(bytes, item.DataPositions[0]);
        var start = 4L + reader.ReadUInt32BigEndian();
        if (start > stream.Length) { Report(ErrorMessages.InvalidExif, ContentFormatErrorCode.InvalidStructure, item.DataPositions[0], item); return; }
        item.ExifData = SliceData(item.Data, start);
    }

    private static IReadOnlyList<BinarySegment> SliceData(IReadOnlyList<BinarySegment> data, long skip)
    {
        var result = new List<BinarySegment>();
        foreach (var segment in data)
        {
            if (skip >= segment.Length) { skip -= segment.Length; continue; }
            result.Add(segment.Slice(skip, segment.Length - skip));
            skip = 0;
        }
        return result.AsReadOnly();
    }

    private void ValidateDerivationGraph()
    {
        var states = new Dictionary<uint, byte>();
        void Visit(HeifItem item, int depth)
        {
            if (states.TryGetValue(item.Id, out var state))
            {
                if (state == 1)
                {
                    Report(ErrorMessages.InvalidGrid, ContentFormatErrorCode.InvalidStructure, item.Info.Offset, item);
                }

                return;
            }
            if (depth > _options.MaxDepth)
            {
                Report(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, item.Info.Offset, item);
            }

            states[item.Id] = 1;
            foreach (var reference in item.ReferenceList)
            {
                if (reference.Type.Value == 0x64696d67)
                {
                    foreach (var id in reference.ToItemIds)
                    {
                        if (GetItem(id) is { } child)
                        {
                            Visit(child, depth + 1);
                        }
                    }
                }
            }

            states[item.Id] = 2;
        }
        foreach (var item in _items)
        {
            Visit(item, 0);
        }
    }

    private void ValidateSave()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // BMFF currently relocates method-0 ranges only inside mdat. Keep other valid file
        // ranges readable, but refuse edits that would silently retain stale coordinates.
        if (_edited)
        {
            foreach (var id in _unrelocatable)
            {
                if (_byId.ContainsKey(id))
                {
                    throw new ContentFormatException(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, _locations?.Offset);
                }
            }
        }
    }
}
