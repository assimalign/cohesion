using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The TrackFragmentRandomAccess box; ISO/IEC 14496-12 8.8.10.</summary>
public sealed class TrackFragmentRandomAccessBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a TrackFragmentRandomAccessBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public TrackFragmentRandomAccessBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.TrackFragmentRandomAccess;

    /// <summary>Gets or sets the track identifier.</summary>
    public uint TrackId { get; set; } = 1;
    /// <summary>Gets or sets the encoded traf number width in bytes (1-4).</summary>
    public byte TrafNumberSize { get; set; } = 1;
    /// <summary>Gets or sets the encoded trun number width in bytes (1-4).</summary>
    public byte TrunNumberSize { get; set; } = 1;
    /// <summary>Gets or sets the encoded sample number width in bytes (1-4).</summary>
    public byte SampleNumberSize { get; set; } = 1;
    /// <summary>Gets or sets the 26 reserved bits.</summary>
    public uint Reserved { get; set; }
    /// <summary>One encoded table entry, stored inline in the entry array.</summary>
    public struct Entry
    {
        /// <summary>Gets or sets the presentation time.</summary>
        public ulong Time { get; set; }
        /// <summary>Gets or sets the absolute moof offset.</summary>
        public ulong MoofOffset { get; set; }
        /// <summary>Gets or sets the one-based traf number.</summary>
        public uint TrafNumber { get; set; }
        /// <summary>Gets or sets the one-based trun number.</summary>
        public uint TrunNumber { get; set; }
        /// <summary>Gets or sets the one-based sample number.</summary>
        public uint SampleNumber { get; set; }
    }
    /// <summary>Gets or sets the ordered random access entries.</summary>
    public Entry[] Entries { get; set; } = Array.Empty<Entry>();
    private int EntrySize => (Version == 1 ? 16 : 8) + TrafNumberSize + TrunNumberSize + SampleNumberSize;

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);
        TrackId = BmffFields.U32(stream);
        var sizes = BmffFields.U32(stream);
        Reserved = sizes >> 6;
        TrafNumberSize = (byte)(((sizes >> 4) & 3) + 1);
        TrunNumberSize = (byte)(((sizes >> 2) & 3) + 1);
        SampleNumberSize = (byte)((sizes & 3) + 1);
        var count = BmffFields.Count(stream, BmffFields.U32(stream), EntrySize, 32);
        Entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            Entries[i] = new Entry { Time = BmffFields.Sized(stream, Version == 1 ? 8 : 4), MoofOffset = BmffFields.Sized(stream, Version == 1 ? 8 : 4), TrafNumber = (uint)BmffFields.Sized(stream, TrafNumberSize), TrunNumber = (uint)BmffFields.Sized(stream, TrunNumberSize), SampleNumber = (uint)BmffFields.Sized(stream, SampleNumberSize) };
        }

        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, TrackId);
        BmffFields.U32(stream, (Reserved << 6) | ((uint)(TrafNumberSize - 1) << 4) | ((uint)(TrunNumberSize - 1) << 2) | (uint)(SampleNumberSize - 1));
        BmffFields.U32(stream, (uint)Entries.Length);
        foreach (var value in Entries)
        {
            BmffFields.Sized(stream, value.Time, Version == 1 ? 8 : 4);
            BmffFields.Sized(stream, value.MoofOffset, Version == 1 ? 8 : 4);
            BmffFields.Sized(stream, value.TrafNumber, TrafNumberSize);
            BmffFields.Sized(stream, value.TrunNumber, TrunNumberSize);
            BmffFields.Sized(stream, value.SampleNumber, SampleNumberSize);
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 16L + (long)EntrySize * Entries.Length + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        if (TrafNumberSize < 1 || TrafNumberSize > 4 || TrunNumberSize < 1 || TrunNumberSize > 4 || SampleNumberSize < 1 || SampleNumberSize > 4 || Reserved > 0x3ffffff || TrackId == 0)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        foreach (var value in Entries)
        {
            BmffFields.Width(value.Time, Version == 1 ? 8 : 4, Offset);
            BmffFields.Width(value.MoofOffset, Version == 1 ? 8 : 4, Offset);
            BmffFields.Width(value.TrafNumber, TrafNumberSize, Offset);
            BmffFields.Width(value.TrunNumber, TrunNumberSize, Offset);
            BmffFields.Width(value.SampleNumber, SampleNumberSize, Offset);
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
