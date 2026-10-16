using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>A still-image or metadata item, with ordered properties, references and borrowed extents.</summary>
public sealed class HeifItem
{
    internal HeifItem(ItemInfoEntryBox info) { Info = info; }
    internal ItemInfoEntryBox Info { get; }
    internal long[] DataPositions { get; set; } = Array.Empty<long>();
    internal List<Property> PropertyList { get; } = new();
    internal List<Reference> ReferenceList { get; } = new();
    /// <summary>Gets the item identifier.</summary>
    public uint Id => Info.ItemId;
    /// <summary>Gets the item FourCC.</summary>
    public FourCC Type => Info.ItemType;
    /// <summary>Gets the item name.</summary>
    public string Name => Info.ItemName;
    /// <summary>Gets the MIME content type, when applicable.</summary>
    public string ContentType => Info.ContentType;
    /// <summary>Gets whether the item is hidden.</summary>
    public bool IsHidden => (Info.Flags & 1) != 0;
    /// <summary>Gets the protection index; nonzero protected items are preserved but unsupported.</summary>
    public ushort ProtectionIndex => Info.ItemProtectionIndex;
    /// <summary>Gets whether the retained container semantics are understood; this does not validate pixels.</summary>
    public bool IsSupported { get; internal set; } = true;
    /// <summary>Gets whether this is a retained coded or derived image type.</summary>
    public bool IsImage => Type.Value is 0x68766331 or 0x68657631 or 0x61763031 or 0x67726964 or 0x696f766c or 0x6964656e;
    /// <summary>Gets the lazy item extents in iloc order.</summary>
    public IReadOnlyList<BinarySegment> Data { get; internal set; } = Array.Empty<BinarySegment>();
    /// <summary>Gets associations in ipma order, including essential flags and missing-property entries.</summary>
    public IReadOnlyList<Property> Properties => PropertyList.AsReadOnly();
    /// <summary>Gets outgoing references in box and target order.</summary>
    public IReadOnlyList<Reference> References => ReferenceList.AsReadOnly();
    /// <summary>Gets the grid descriptor, or null for other items or invalid descriptors.</summary>
    public HeifGridImage? Grid { get; internal set; }
    /// <summary>Gets the overlay descriptor, or null for other items or invalid descriptors.</summary>
    public HeifOverlayImage? Overlay { get; internal set; }
    /// <summary>Gets the identity derivation's single source, or null.</summary>
    public HeifItem? IdentitySource { get; internal set; }
    /// <summary>Gets Exif bytes beginning at the TIFF header, across extents without copying; otherwise empty.</summary>
    public IReadOnlyList<BinarySegment> ExifData { get; internal set; } = Array.Empty<BinarySegment>();

    /// <summary>An item property association in encoded order.</summary>
    public readonly struct Property
    {
        internal Property(ushort index, bool essential, BmffBox? box) { Index = index; Essential = essential; Box = box; }
        /// <summary>Gets the one-based ipco index; zero denotes no property.</summary>
        public ushort Index { get; }
        /// <summary>Gets the essential flag.</summary>
        public bool Essential { get; }
        /// <summary>Gets the property box, or null for zero or missing indices.</summary>
        public BmffBox? Box { get; }
    }

    /// <summary>An outgoing item reference with its ordered destination IDs.</summary>
    public readonly struct Reference
    {
        internal Reference(FourCC type, uint[] ids) { Type = type; ToItemIds = Array.AsReadOnly(ids); }
        /// <summary>Gets the reference FourCC.</summary>
        public FourCC Type { get; }
        /// <summary>Gets destination IDs in wire order.</summary>
        public IReadOnlyList<uint> ToItemIds { get; }
    }
}
