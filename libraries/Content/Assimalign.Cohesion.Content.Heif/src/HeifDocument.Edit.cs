using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

public sealed partial class HeifDocument
{
    /// <summary>Removes all Exif item descriptors, locations and understood references.</summary>
    /// <returns>The number of items removed.</returns>
    /// <exception cref="ContentFormatException">An item is the primary item or the structure cannot be edited.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    /// <remarks>Unreferenced bytes in shared data boxes are retained; this is not secure erasure.</remarks>
    public int RemoveExif() => RemoveMetadata(false);

    /// <summary>Removes all XMP item descriptors, locations and understood references.</summary>
    /// <returns>The number of items removed.</returns>
    /// <exception cref="ContentFormatException">An item is the primary item or the structure cannot be edited.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    /// <remarks>Unreferenced bytes in shared data boxes are retained; this is not secure erasure.</remarks>
    public int RemoveXmp() => RemoveMetadata(true);

    /// <summary>Replaces an Exif item's entire encoded payload, including its four-byte TIFF-header offset.</summary>
    /// <param name="itemId">An existing Exif item.</param>
    /// <param name="payload">Borrowed bytes; the TIFF header begins four plus the encoded offset bytes from the start.</param>
    /// <exception cref="ContentFormatException">The item or TIFF offset is invalid, or a limit is exceeded.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    public void ReplaceExif(uint itemId, BinarySegment payload)
    {
        var item = Editable(itemId);
        if (item.Type.Value != 0x45786966)
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidValue, item.Info.Offset);
        }

        if (payload.Length < 4)
        {
            throw new ContentFormatException(ErrorMessages.InvalidExif, ContentFormatErrorCode.Truncated, item.Info.Offset);
        }

        Span<byte> prefix = stackalloc byte[4];
        using (var stream = payload.OpenRead())
        {
            stream.ReadContentExactly(prefix);
        }

        var reader = new BinaryContentReader(prefix);
        if (4L + reader.ReadUInt32BigEndian() > payload.Length)
        {
            throw new ContentFormatException(ErrorMessages.InvalidExif, ContentFormatErrorCode.InvalidStructure, item.Info.Offset);
        }

        ReplaceData(item, payload);
        ReadExif(item);
    }

    /// <summary>Replaces an XMP item's opaque XML bytes without parsing XML.</summary>
    /// <param name="itemId">An existing XMP item.</param>
    /// <param name="payload">The borrowed replacement bytes.</param>
    /// <exception cref="ContentFormatException">The item is not XMP or a limit is exceeded.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    public void ReplaceXmp(uint itemId, BinarySegment payload)
    {
        var item = Editable(itemId);
        if (!IsXmp(item))
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidValue, item.Info.Offset);
        }

        ReplaceData(item, payload);
    }

    /// <summary>Adds a coded thumbnail item and its thmb reference to an existing image.</summary>
    /// <param name="imageId">The master image ID.</param>
    /// <param name="itemType">hvc1, hev1 or av01.</param>
    /// <param name="payload">Borrowed encoded thumbnail data.</param>
    /// <param name="properties">Ordered properties, including ispe and the matching codec configuration.</param>
    /// <returns>The newly created thumbnail.</returns>
    /// <exception cref="ArgumentNullException">The property list is null.</exception>
    /// <exception cref="ContentFormatException">The master, type, properties or resource counts are invalid.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    public HeifItem AddThumbnail(uint imageId, FourCC itemType, BinarySegment payload, IReadOnlyList<BmffBox> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var master = Editable(imageId);
        if (!master.IsImage || itemType.Value is not (0x68766331 or 0x68657631 or 0x61763031) || payload.IsEmpty)
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidValue, master.Info.Offset);
        }

        if (_items.Count >= _options.MaxItemCount || properties.Count > 255 || properties.Count > _options.MaxPropertyCount - _propertyContainer!.Children.Count || properties.Count > 32767 - _propertyContainer.Children.Count)
        {
            throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, master.Info.Offset);
        }

        var spatial = false;
        var configuration = false;
        foreach (var property in properties)
        {
            if (!Understood(property))
            {
                throw new ContentFormatException(ErrorMessages.Unsupported, ContentFormatErrorCode.Unsupported, master.Info.Offset);
            }

            _ = property.GetPayloadSize();
            spatial |= property is ImageSpatialExtentsBox;
            configuration |= itemType.Value == 0x61763031 ? property is Av1ConfigurationBox : property is HevcConfigurationBox;
        }
        if (!spatial || !configuration)
        {
            throw new ContentFormatException(ErrorMessages.MissingProperty, ContentFormatErrorCode.InvalidStructure, master.Info.Offset);
        }

        uint id = 1;
        foreach (var item in _items)
        {
            if (item.Id == uint.MaxValue)
            {
                throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, item.Info.Offset);
            }

            id = Math.Max(id, item.Id + 1);
        }
        var info = new ItemInfoEntryBox { ItemId = id, Version = (byte)(id > ushort.MaxValue ? 3 : 2), ItemType = itemType, ItemName = "Thumbnail" };
        var thumbnail = new HeifItem(info);
        // Ensure appending data cannot overflow before adding any structural nodes.
        CheckAppend(payload.Length);
        _info!.Children.Add(info);
        if (_info.Children.Count > ushort.MaxValue)
        {
            _info.Version = 1;
        }

        _items.Add(thumbnail);
        _byId.Add(id, thumbnail);
        ReplaceData(thumbnail, payload);
        foreach (var property in properties)
        {
            Associate(thumbnail, property, null);
        }

        _references ??= AddMeta(new ItemReferenceBox());
        if (id > ushort.MaxValue && _references.Version == 0)
        {
            _references.Version = 1;
            foreach (var box in _references.Children)
            {
                var fields = ItemReferenceBox.GetFields(box);
                if (fields is not null)
                {
                    fields.LargeIds = true;
                }
            }
        }
        var reference = new ThumbnailReferenceBox { LargeIds = _references.Version == 1, FromItemId = id, ToItemIds = [imageId] };
        _references.Children.Add(reference);
        thumbnail.ReferenceList.Add(new HeifItem.Reference(FourCC.FromUInt32(0x74686d62), [imageId]));
        return thumbnail;
    }

    /// <summary>Removes a thumbnail item and its understood relationships.</summary>
    /// <param name="itemId">The thumbnail ID.</param>
    /// <exception cref="ContentFormatException">The ID is not a thumbnail or is primary.</exception>
    /// <exception cref="ObjectDisposedException">The view is disposed.</exception>
    public void RemoveThumbnail(uint itemId)
    {
        var item = Editable(itemId);
        var thumbnail = false;
        foreach (var reference in item.ReferenceList)
        {
            thumbnail |= reference.Type.Value == 0x74686d62;
        }

        if (!thumbnail)
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidValue, item.Info.Offset);
        }

        RemoveItem(item);
    }

    /// <summary>Sets the counter-clockwise rotation, replacing this item's association without changing shared properties.</summary>
    /// <param name="itemId">The image item.</param>
    /// <param name="quarterTurns">Zero through three counter-clockwise quarter turns.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rotation is outside zero through three.</exception>
    /// <exception cref="ContentFormatException">The item is not editable or a property limit is exceeded.</exception>
    public void SetRotation(uint itemId, byte quarterTurns)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quarterTurns, (byte)3);
        var item = Editable(itemId);
        if (!item.IsImage)
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidValue, item.Info.Offset);
        }

        Associate(item, new ImageRotationBox { Angle = quarterTurns }, 0x69726f74);
    }

    /// <summary>Sets or removes mirroring without changing another item's shared property.</summary>
    /// <param name="itemId">The image item.</param>
    /// <param name="axis">Zero for vertical, one for horizontal, or null to remove mirroring.</param>
    /// <exception cref="ArgumentOutOfRangeException">The axis exceeds one.</exception>
    /// <exception cref="ContentFormatException">The item is not editable or a property limit is exceeded.</exception>
    public void SetMirror(uint itemId, byte? axis)
    {
        if (axis.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(axis.Value, (byte)1);
        }

        var item = Editable(itemId);
        if (!item.IsImage)
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidValue, item.Info.Offset);
        }

        Associate(item, axis.HasValue ? new ImageMirrorBox { Axis = axis.Value } : null, 0x696d6972);
    }

    private HeifItem Editable(uint id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_info is null || _locations is null || _properties is null || _propertyContainer is null || GetItem(id) is not { } item)
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidStructure, _meta?.Offset);
        }

        return item;
    }
    private T AddMeta<T>(T box) where T : BmffBox { _meta!.Children.Add(box); return box; }
    private int RemoveMetadata(bool xmp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var matches = MetadataItems(xmp);
        foreach (var item in matches)
        {
            RemoveItem(Editable(item.Id));
        }

        return matches.Count;
    }
    private void RemoveItem(HeifItem item)
    {
        if (PrimaryItem == item)
        {
            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidStructure, item.Info.Offset);
        }
        // Do not silently corrupt a derived image by deleting one of its inputs.
        foreach (var other in _items)
        {
            foreach (var reference in other.ReferenceList)
            {
                if (reference.Type.Value == 0x64696d67)
                {
                    foreach (var target in reference.ToItemIds)
                    {
                        if (target == item.Id)
                        {
                            throw new ContentFormatException(ErrorMessages.InvalidEdit, ContentFormatErrorCode.InvalidStructure, other.Info.Offset);
                        }
                    }
                }
            }
        }

        _info!.Children.Remove(item.Info);
        var locations = new List<ItemLocationBox.Entry>();
        foreach (var entry in _locations!.Entries)
        {
            if (entry.ItemId != item.Id)
            {
                locations.Add(entry);
            }
        }

        _locations.Entries = locations.ToArray();
        foreach (var box in _properties!.Children)
        {
            if (box is not ItemPropertyAssociationBox ipma)
            {
                continue;
            }

            var entries = new List<ItemPropertyAssociationBox.Entry>();
            foreach (var entry in ipma.Entries)
            {
                if (entry.ItemId != item.Id)
                {
                    entries.Add(entry);
                }
            }

            ipma.Entries = entries.ToArray();
        }
        if (_references is not null)
        {
            for (var i = _references.Children.Count - 1; i >= 0; i--)
            {
                var fields = ItemReferenceBox.GetFields(_references.Children[i]);
                if (fields is null)
                {
                    continue;
                }

                if (fields.FromItemId == item.Id) { _references.Children.RemoveAt(i); continue; }
                var targets = new List<uint>();
                foreach (var target in fields.ToItemIds)
                {
                    if (target != item.Id)
                    {
                        targets.Add(target);
                    }
                }

                fields.ToItemIds = targets.ToArray();
                if (targets.Count == 0)
                {
                    _references.Children.RemoveAt(i);
                }
            }
        }
        _items.Remove(item);
        _byId.Remove(item.Id);
        foreach (var remaining in _items)
        {
            remaining.ReferenceList.Clear();
        }

        ReadReferences();
        _edited = true;
    }

    private void CheckAppend(long length)
    {
        var size = _idat?.Payload.Length ?? 0;
        if (length == 0 || length > long.MaxValue - size || _appendedData.Count >= _options.MaxExtentCount)
        {
            throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, _meta?.Offset);
        }
    }
    private void ReplaceData(HeifItem item, BinarySegment payload)
    {
        CheckAppend(payload.Length);
        _idat ??= AddMeta(new ItemDataBox());
        var offset = _idat.Payload.Length;
        _appendedData.Add(payload);
        var all = new List<BinarySegment>(_appendedData.Count + 1) { _originalIdat };
        all.AddRange(_appendedData);
        var stream = new HeifSegmentStream(all);
        // No item segment references these concatenation views: each retains its own source.
        foreach (var old in _ownedStreams)
        {
            old.Dispose();
        }

        _ownedStreams.Clear();
        _ownedStreams.Add(stream);
        _idat.Payload = BinarySegment.Create(stream, 0, stream.Length);
        var entries = new List<ItemLocationBox.Entry>(_locations!.Entries.Length + 1);
        foreach (var entry in _locations.Entries)
        {
            if (entry.ItemId != item.Id)
            {
                entries.Add(entry);
            }
        }

        entries.Add(new ItemLocationBox.Entry
        {
            ItemId = item.Id, ConstructionMethod = 1,
            Extents = [new() { Offset = (ulong)offset, Length = (ulong)payload.Length }]
        });
        _locations.Entries = entries.ToArray();
        _locations.Version = (byte)Math.Max(_locations.Version, item.Id > ushort.MaxValue ? 2 : 1);
        _locations.OffsetSize = 8;
        _locations.LengthSize = 8;
        item.Data = Array.AsReadOnly(new[] { payload });
        item.DataPositions = [_idat.Offset + _idat.Header.HeaderSize + offset];
        _unrelocatable.Remove(item.Id);
        _edited = true;
    }

    private void Associate(HeifItem item, BmffBox? property, uint? replaceType)
    {
        var values = new List<HeifItem.Property>(item.PropertyList);
        var insert = values.Count;
        if (replaceType.HasValue)
        {
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (values[i].Box is { } old && (uint)old.BoxType == replaceType.Value) { insert = i; values.RemoveAt(i); }
            }
        }
        if (property is not null)
        {
            if (values.Count >= 255 || _propertyContainer!.Children.Count >= Math.Min(32767, _options.MaxPropertyCount))
            {
                throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, item.Info.Offset);
            }

            _propertyContainer.Children.Add(property);
            values.Insert(insert, new HeifItem.Property((ushort)_propertyContainer.Children.Count, true, property));
        }
        // Consolidate only this item's associations; every other entry keeps its wire ordering.
        foreach (var box in _properties!.Children)
        {
            if (box is not ItemPropertyAssociationBox ipma)
            {
                continue;
            }

            var retained = new List<ItemPropertyAssociationBox.Entry>();
            foreach (var entry in ipma.Entries)
            {
                if (entry.ItemId != item.Id)
                {
                    retained.Add(entry);
                }
            }

            ipma.Entries = retained.ToArray();
        }
        var encoded = new ItemPropertyAssociationBox.Association[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            encoded[i] = new() { PropertyIndex = values[i].Index, Essential = values[i].Essential };
        }

        _properties.Children.Add(new ItemPropertyAssociationBox
        {
            Version = (byte)(item.Id > ushort.MaxValue ? 1 : 0), Flags = 1,
            Entries = [new() { ItemId = item.Id, Associations = encoded }]
        });
        item.PropertyList.Clear();
        item.PropertyList.AddRange(values);
        _edited = true;
    }
}
