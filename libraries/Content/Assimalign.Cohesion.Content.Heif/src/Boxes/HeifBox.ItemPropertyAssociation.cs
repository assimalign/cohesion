using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The ipma full box, including 16/32-bit IDs and 7/15-bit property indices.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ItemPropertyAssociationBox : BmffFullBox
{
    /// <summary>Creates an empty ItemPropertyAssociationBox model.</summary>
    public ItemPropertyAssociationBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x69706d61;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();

    /// <summary>An ordered property index and its essential flag.</summary>
    public struct Association
    {
        /// <summary>Gets or sets the one-based property index; zero means no property.</summary>
        public ushort PropertyIndex { get; set; }
        /// <summary>Gets or sets whether the property is required to interpret the item.</summary>
        public bool Essential { get; set; }
    }
    /// <summary>An item's ordered associations.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the item identifier.</summary>
        public uint ItemId { get; set; }
        /// <summary>Gets or sets the associations in encoded order.</summary>
        public Association[] Associations { get; set; }
    }
    /// <summary>Gets or sets the ordered item associations.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        HeifFields.Version(Version, Flags, 1, 1, stream.Offset);
        var count = HeifFields.Count(HeifFields.Read(stream, 4), Options.MaxItemCount, stream.Remaining, Version == 0 ? 3 : 5, stream.Offset + stream.Position);
        if ((long)count * 16 > Options.MaxBufferedPayloadSize)
        {
            throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, stream.Offset);
        }

        Entries = new Entry[count];
        long allocated = (long)count * 16;
        for (var i = 0; i < count; i++)
        {
            var id = HeifFields.Read(stream, Version == 0 ? 2 : 4);
            var length = HeifFields.Count(HeifFields.Read(stream, 1), Options.MaxPropertyCount, stream.Remaining, (Flags & 1) == 0 ? 1 : 2, stream.Offset + stream.Position);
            allocated += length * 4L;
            if (allocated > Options.MaxBufferedPayloadSize)
            {
                throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, stream.Offset + stream.Position);
            }

            var values = new Association[length];
            for (var j = 0; j < length; j++)
            {
                var value = HeifFields.Read(stream, (Flags & 1) == 0 ? 1 : 2);
                var mask = (Flags & 1) == 0 ? 127u : 32767u;
                values[j] = new Association { PropertyIndex = (ushort)(value & mask), Essential = (value & (mask + 1)) != 0 };
            }
            Entries[i] = new Entry { ItemId = id, Associations = values };
        }
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        HeifFields.Write(stream, (uint)Entries.Length, 4);
        foreach (var entry in Entries)
        {
            HeifFields.Write(stream, entry.ItemId, Version == 0 ? 2 : 4);
            HeifFields.Write(stream, (uint)entry.Associations.Length, 1);
            foreach (var value in entry.Associations)
            {
                HeifFields.Write(stream, (uint)(value.PropertyIndex | (value.Essential ? ((Flags & 1) == 0 ? 128 : 32768) : 0)), (Flags & 1) == 0 ? 1 : 2);
            }
        }
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        long size = 8L + TrailingData.Length;
        foreach (var entry in Entries)
        {
            size += (Version == 0 ? 3 : 5) + entry.Associations.Length * ((Flags & 1) == 0 ? 1L : 2L);
        }

        return size;
    }
    private void Validate()
    {
        HeifFields.Version(Version, Flags, 1, 1, Offset);
        foreach (var entry in Entries)
        {
            if ((Version == 0 && entry.ItemId > ushort.MaxValue) || entry.Associations.Length > 255)
            {
                throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
            }

            foreach (var value in entry.Associations)
            {
                if (value.PropertyIndex > ((Flags & 1) == 0 ? 127 : 32767))
                {
                    throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
                }
            }
        }
    }
}
