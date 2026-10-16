using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The CompactSampleSize box; ISO/IEC 14496-12 8.7.3.3.</summary>
public sealed class CompactSampleSizeBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a CompactSampleSizeBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public CompactSampleSizeBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.CompactSampleSize;

    /// <summary>Gets or sets the 24 reserved bits.</summary>
    public uint Reserved { get; set; }
    /// <summary>Gets or sets the number of bits per sample size (4, 8 or 16).</summary>
    public byte FieldSize { get; set; } = 8;
    /// <summary>Gets or sets the sample sizes.</summary>
    public ushort[] Entries { get; set; } = Array.Empty<ushort>();
    private byte _paddingNibble;

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        Reserved = (uint)BmffFields.Sized(stream, 3);
        FieldSize = BmffFields.U8(stream);
        Validate();
        var count = BmffFields.Count(stream, BmffFields.U32(stream), 0, 2);
        var byteCount = ((long)count * FieldSize + 7) / 8;
        if (byteCount > stream.Remaining)
        {
            throw new ContentFormatException(ErrorMessages.EntryBounds, ContentFormatErrorCode.InvalidStructure, stream.Offset + stream.Position);
        }

        Entries = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            if (FieldSize == 16)
            {
                Entries[i] = BmffFields.U16(stream);
            }
            else if (FieldSize == 8)
            {
                Entries[i] = BmffFields.U8(stream);
            }
            else
            {
                var value = BmffFields.U8(stream);
                Entries[i] = (ushort)(value >> 4);
                if (++i < count)
                {
                    Entries[i] = (ushort)(value & 15);
                }
                else
                {
                    _paddingNibble = (byte)(value & 15);
                }
            }
        }
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.Sized(stream, Reserved, 3);
        BmffFields.U8(stream, FieldSize);
        BmffFields.U32(stream, (uint)Entries.Length);
        for (var i = 0; i < Entries.Length; i++)
        {
            if (FieldSize == 16)
            {
                BmffFields.U16(stream, Entries[i]);
            }
            else if (FieldSize == 8)
            {
                BmffFields.U8(stream, (byte)Entries[i]);
            }
            else
            {
                var high = Entries[i] << 4;
                var low = ++i < Entries.Length ? Entries[i] : _paddingNibble;
                BmffFields.U8(stream, (byte)(high | low));
            }
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 12L + (Entries.LongLength * FieldSize + 7) / 8 + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        if (FieldSize != 4 && FieldSize != 8 && FieldSize != 16)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        BmffFields.Width(Reserved, 3, Offset);
        foreach (var value in Entries)
        {
            if (value >= (1u << FieldSize))
            {
                throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
            }
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
