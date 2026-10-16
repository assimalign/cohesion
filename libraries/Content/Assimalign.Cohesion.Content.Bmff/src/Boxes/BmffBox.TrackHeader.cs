using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The TrackHeader box; ISO/IEC 14496-12 8.3.2.</summary>
public sealed class TrackHeaderBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a TrackHeaderBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public TrackHeaderBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.TrackHeader;

    /// <summary>Gets or sets creation time in whole UTC seconds since 1904-01-01.</summary>
    public ulong CreationTime { get; set; }
    /// <summary>Gets or sets modification time in whole UTC seconds since 1904-01-01.</summary>
    public ulong ModificationTime { get; set; }
    /// <summary>Gets or sets the nonzero track identifier.</summary>
    public uint TrackId { get; set; } = 1;
    /// <summary>Gets or sets the first reserved word.</summary>
    public uint Reserved1 { get; set; }
    /// <summary>Gets or sets the duration in timescale units, including the all-ones unknown value.</summary>
    public ulong Duration { get; set; }
    /// <summary>Gets or sets the eight reserved bytes.</summary>
    public byte[] Reserved { get; set; } = new byte[8];
    /// <summary>Gets or sets the visual stacking layer.</summary>
    public short Layer { get; set; }
    /// <summary>Gets or sets the alternate track group.</summary>
    public short AlternateGroup { get; set; }
    /// <summary>Gets or sets the track volume.</summary>
    public FixedPoint8x8 Volume { get; set; }
    /// <summary>Gets or sets the reserved word after volume.</summary>
    public ushort Reserved2 { get; set; }
    /// <summary>Gets or sets the nine raw matrix words (16.16 and 2.30 as defined by the specification).</summary>
    public int[] Matrix { get; set; } = [0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000];
    /// <summary>Gets or sets the presentation width.</summary>
    public UnsignedFixedPoint16x16 Width { get; set; }
    /// <summary>Gets or sets the presentation height.</summary>
    public UnsignedFixedPoint16x16 Height { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 15, stream.Offset);
        CreationTime = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        ModificationTime = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        TrackId = BmffFields.U32(stream);
        Reserved1 = BmffFields.U32(stream);
        Duration = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        Reserved = BmffFields.Bytes(stream, 8);
        Layer = BmffFields.I16(stream);
        AlternateGroup = BmffFields.I16(stream);
        Volume = FixedPoint8x8.FromRaw(BmffFields.I16(stream));
        Reserved2 = BmffFields.U16(stream);
        for (var i = 0; i < 9; i++)
        {
            Matrix[i] = BmffFields.I32(stream);
        }

        Width = UnsignedFixedPoint16x16.FromRaw(BmffFields.U32(stream));
        Height = UnsignedFixedPoint16x16.FromRaw(BmffFields.U32(stream));
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.Sized(stream, CreationTime, Version == 1 ? 8 : 4);
        BmffFields.Sized(stream, ModificationTime, Version == 1 ? 8 : 4);
        BmffFields.U32(stream, TrackId);
        BmffFields.U32(stream, Reserved1);
        BmffFields.Sized(stream, Duration, Version == 1 ? 8 : 4);
        stream.Write(Reserved);
        BmffFields.I16(stream, Layer);
        BmffFields.I16(stream, AlternateGroup);
        BmffFields.I16(stream, Volume.RawValue);
        BmffFields.U16(stream, Reserved2);
        foreach (var value in Matrix)
        {
            BmffFields.I32(stream, value);
        }

        BmffFields.U32(stream, Width.RawValue);
        BmffFields.U32(stream, Height.RawValue);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return (Version == 1 ? 96L : 84L) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 15, Offset);
        BmffFields.Width(CreationTime, Version == 1 ? 8 : 4, Offset);
        BmffFields.Width(ModificationTime, Version == 1 ? 8 : 4, Offset);
        BmffFields.Width(Duration, Version == 1 ? 8 : 4, Offset);
        if (TrackId == 0)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        BmffFields.ArrayLength(Reserved, 8, Offset);
        BmffFields.ArrayLength(Matrix, 9, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
