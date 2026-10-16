using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SampleGroupDescription box; ISO/IEC 14496-12 8.9.3.</summary>
public sealed class SampleGroupDescriptionBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SampleGroupDescriptionBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SampleGroupDescriptionBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SampleGroupDescription;

    /// <summary>Gets or sets the grouping type.</summary>
    public FourCC GroupingType { get; set; }
    /// <summary>Gets or sets the v1/v2 common entry length, or zero for per-entry lengths.</summary>
    public uint DefaultLength { get; set; }
    /// <summary>Gets or sets the v2 default description index.</summary>
    public uint DefaultSampleDescriptionIndex { get; set; }
    /// <summary>Gets or sets the uninterpreted group descriptions.</summary>
    public byte[][] Entries { get; set; } = Array.Empty<byte[]>();
    private int FixedLength => GroupingType.Value switch { 0x726f6c6c or 0x70726f6c => 2, 0x72617020 or 0x73796e63 or 0x74656c65 => 1, _ => 0 };
    private long EntryBytes()
    {
        long size = 0;
        foreach (var value in Entries)
        {
            size += value.LongLength + (Version >= 1 && DefaultLength == 0 ? 4 : 0);
        }

        return size;
    }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 2, 0, stream.Offset);
        GroupingType = FourCC.FromUInt32(BmffFields.U32(stream));
        if (Version >= 1)
        {
            DefaultLength = BmffFields.U32(stream);
        }

        if (Version >= 2)
        {
            DefaultSampleDescriptionIndex = BmffFields.U32(stream);
        }

        var declared = BmffFields.U32(stream);
        if (Version == 0 && FixedLength == 0 && declared != 0)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, stream.Offset + stream.Position);
        }

        var count = BmffFields.Count(stream, declared, Version == 0 ? FixedLength : DefaultLength == 0 ? 4 : DefaultLength > int.MaxValue ? int.MaxValue : (int)DefaultLength, 8);
        Entries = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var length = Version == 0 ? (uint)FixedLength : DefaultLength == 0 ? BmffFields.U32(stream) : DefaultLength;
            Entries[i] = BmffFields.Bytes(stream, length);
        }
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, GroupingType.Value);
        if (Version >= 1)
        {
            BmffFields.U32(stream, DefaultLength);
        }

        if (Version >= 2)
        {
            BmffFields.U32(stream, DefaultSampleDescriptionIndex);
        }

        BmffFields.U32(stream, (uint)Entries.Length);
        foreach (var value in Entries)
        {
            if (Version >= 1 && DefaultLength == 0)
            {
                BmffFields.U32(stream, (uint)value.Length);
            }

            stream.Write(value);
        }
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 12L + (Version >= 1 ? 4 : 0) + (Version >= 2 ? 4 : 0) + EntryBytes() + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 2, 0, Offset);
        ArgumentNullException.ThrowIfNull(Entries);
        if (Version == 0 && FixedLength == 0 && Entries.Length != 0)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
        }

        foreach (var value in Entries)
        {
            ArgumentNullException.ThrowIfNull(value);
            if ((Version == 0 && value.Length != FixedLength) || (Version >= 1 && DefaultLength != 0 && value.LongLength != DefaultLength))
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
