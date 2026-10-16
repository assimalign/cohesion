using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The ItemLocation box; ISO/IEC 14496-12 8.11.3.</summary>
public sealed class ItemLocationBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a ItemLocationBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public ItemLocationBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.ItemLocation;

    /// <summary>Gets or sets the extent-offset width (0, 4 or 8 bytes).</summary>
    public byte OffsetSize { get; set; } = 4;
    /// <summary>Gets or sets the extent-length width (0, 4 or 8 bytes).</summary>
    public byte LengthSize { get; set; } = 4;
    /// <summary>Gets or sets the base-offset width (0, 4 or 8 bytes).</summary>
    public byte BaseOffsetSize { get; set; } = 4;
    /// <summary>Gets or sets the v1/v2 extent-index width (0, 4 or 8 bytes); v0 retains this reserved nibble.</summary>
    public byte IndexSize { get; set; }
    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Extent
    {
        /// <summary>Gets or sets the extent index for v1/v2.</summary>
        public ulong Index { get; set; }
        /// <summary>Gets or sets the offset from the item base.</summary>
        public ulong Offset { get; set; }
        /// <summary>Gets or sets the extent length.</summary>
        public ulong Length { get; set; }
    }
    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the item identifier.</summary>
        public uint ItemId { get; set; }
        /// <summary>Gets or sets the v1/v2 construction method (0 file, 1 idat, 2 item).</summary>
        public ushort ConstructionMethod { get; set; }
        /// <summary>Gets or sets the twelve construction-method reserved bits.</summary>
        public ushort Reserved { get; set; }
        /// <summary>Gets or sets zero for this file or the one-based data reference index.</summary>
        public ushort DataReferenceIndex { get; set; }
        /// <summary>Gets or sets the item base offset.</summary>
        public ulong BaseOffset { get; set; }
        /// <summary>Gets or sets the item extents.</summary>
        public Extent[] Extents { get; set; }
    }
    /// <summary>Gets or sets the item locations.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();
    private int ExtentSize => OffsetSize + LengthSize + (Version == 0 ? 0 : IndexSize);
    private int ItemSize => (Version == 2 ? 4 : 2) + (Version == 0 ? 0 : 2) + 2 + BaseOffsetSize + 2;
    private long EntryBytes()
    {
        long size = 0;
        foreach (var value in Entries)
        {
            size += ItemSize + (long)ExtentSize * value.Extents.Length;
        }

        return size;
    }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 2, 0, stream.Offset);
        var sizes = BmffFields.U8(stream);
        OffsetSize = (byte)(sizes >> 4);
        LengthSize = (byte)(sizes & 15);
        sizes = BmffFields.U8(stream);
        BaseOffsetSize = (byte)(sizes >> 4);
        IndexSize = (byte)(sizes & 15);
        Validate();
        var count = BmffFields.Count(stream, Version == 2 ? BmffFields.U32(stream) : BmffFields.U16(stream), ItemSize, 32);
        Entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var value = new Entry { ItemId = Version == 2 ? BmffFields.U32(stream) : BmffFields.U16(stream) };
            if (Version != 0)
            {
                var method = BmffFields.U16(stream);
                value.Reserved = (ushort)(method >> 4);
                value.ConstructionMethod = (ushort)(method & 15);
            }
            value.DataReferenceIndex = BmffFields.U16(stream);
            value.BaseOffset = BmffFields.Sized(stream, BaseOffsetSize);
            var extents = BmffFields.Count(stream, BmffFields.U16(stream), ExtentSize, 24);
            value.Extents = new Extent[extents];
            for (var j = 0; j < extents; j++)
            {
                value.Extents[j] = new Extent { Index = Version == 0 ? 0 : BmffFields.Sized(stream, IndexSize), Offset = BmffFields.Sized(stream, OffsetSize), Length = BmffFields.Sized(stream, LengthSize) };
            }

            Entries[i] = value;
        }
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U8(stream, (byte)((OffsetSize << 4) | LengthSize));
        BmffFields.U8(stream, (byte)((BaseOffsetSize << 4) | IndexSize));
        BmffFields.Sized(stream, (uint)Entries.Length, Version == 2 ? 4 : 2);
        foreach (var value in Entries)
        {
            BmffFields.Sized(stream, value.ItemId, Version == 2 ? 4 : 2);
            if (Version != 0)
            {
                BmffFields.U16(stream, (ushort)((value.Reserved << 4) | value.ConstructionMethod));
            }

            BmffFields.U16(stream, value.DataReferenceIndex);
            BmffFields.Sized(stream, value.BaseOffset, BaseOffsetSize);
            BmffFields.U16(stream, (ushort)value.Extents.Length);
            foreach (var extent in value.Extents)
            {
                if (Version != 0)
                {
                    BmffFields.Sized(stream, extent.Index, IndexSize);
                }

                BmffFields.Sized(stream, extent.Offset, OffsetSize);
                BmffFields.Sized(stream, extent.Length, LengthSize);
            }
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return (Version == 2 ? 10L : 8L) + EntryBytes() + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 2, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        if ((OffsetSize != 0 && OffsetSize != 4 && OffsetSize != 8) || (LengthSize != 0 && LengthSize != 4 && LengthSize != 8) ||
            (BaseOffsetSize != 0 && BaseOffsetSize != 4 && BaseOffsetSize != 8) || (Version != 0 && IndexSize != 0 && IndexSize != 4 && IndexSize != 8) || IndexSize > 15)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        BmffFields.Width((ulong)Entries.Length, Version == 2 ? 4 : 2, Offset);
        foreach (var value in Entries)
        {
            ArgumentNullException.ThrowIfNull(value.Extents);
            BmffFields.Width(value.ItemId, Version == 2 ? 4 : 2, Offset);
            BmffFields.Width(value.BaseOffset, BaseOffsetSize, Offset);
            if (value.ConstructionMethod > 2 || value.Reserved > 0xfff || value.Extents.Length > ushort.MaxValue || (Version == 0 && value.ConstructionMethod != 0))
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }

            foreach (var extent in value.Extents)
            {
                if (Version != 0)
                {
                    BmffFields.Width(extent.Index, IndexSize, Offset);
                }

                BmffFields.Width(extent.Offset, OffsetSize, Offset);
                BmffFields.Width(extent.Length, LengthSize, Offset);
            }
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
