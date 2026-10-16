using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The TrackFragmentHeader box; ISO/IEC 14496-12 8.8.7.</summary>
public sealed class TrackFragmentHeaderBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a TrackFragmentHeaderBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public TrackFragmentHeaderBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.TrackFragmentHeader;

    /// <summary>Gets or sets the nonzero track identifier.</summary>
    public uint TrackId { get; set; } = 1;
    /// <summary>Gets or sets the absolute base data offset when its flag is set.</summary>
    public ulong? BaseDataOffset { get; set; }
    /// <summary>Gets or sets the description index when its flag is set.</summary>
    public uint? SampleDescriptionIndex { get; set; }
    /// <summary>Gets or sets the default duration when its flag is set.</summary>
    public uint? DefaultSampleDuration { get; set; }
    /// <summary>Gets or sets the default size when its flag is set.</summary>
    public uint? DefaultSampleSize { get; set; }
    /// <summary>Gets or sets the default flags when its flag is set.</summary>
    public uint? DefaultSampleFlags { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0x03003b, stream.Offset);
        TrackId = BmffFields.U32(stream);
        BaseDataOffset = (Flags & 1) != 0 ? BmffFields.U64(stream) : null;
        SampleDescriptionIndex = (Flags & 2) != 0 ? BmffFields.U32(stream) : null;
        DefaultSampleDuration = (Flags & 8) != 0 ? BmffFields.U32(stream) : null;
        DefaultSampleSize = (Flags & 16) != 0 ? BmffFields.U32(stream) : null;
        DefaultSampleFlags = (Flags & 32) != 0 ? BmffFields.U32(stream) : null;
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, TrackId);
        if (BaseDataOffset.HasValue)
        {
            BmffFields.U64(stream, BaseDataOffset.Value);
        }

        if (SampleDescriptionIndex.HasValue)
        {
            BmffFields.U32(stream, SampleDescriptionIndex.Value);
        }

        if (DefaultSampleDuration.HasValue)
        {
            BmffFields.U32(stream, DefaultSampleDuration.Value);
        }

        if (DefaultSampleSize.HasValue)
        {
            BmffFields.U32(stream, DefaultSampleSize.Value);
        }

        if (DefaultSampleFlags.HasValue)
        {
            BmffFields.U32(stream, DefaultSampleFlags.Value);
        }

        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 8L + (BaseDataOffset.HasValue ? 8 : 0) + (SampleDescriptionIndex.HasValue ? 4 : 0) + (DefaultSampleDuration.HasValue ? 4 : 0) + (DefaultSampleSize.HasValue ? 4 : 0) + (DefaultSampleFlags.HasValue ? 4 : 0) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0x03003b, Offset);
        if (TrackId == 0 || ((Flags & 1) != 0 && (Flags & 0x020000) != 0))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        if (BaseDataOffset.HasValue != ((Flags & 1) != 0))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        if (SampleDescriptionIndex.HasValue != ((Flags & 2) != 0))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        if (DefaultSampleDuration.HasValue != ((Flags & 8) != 0))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        if (DefaultSampleSize.HasValue != ((Flags & 16) != 0))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        if (DefaultSampleFlags.HasValue != ((Flags & 32) != 0))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" track-id={TrackId} base-data-offset={BaseDataOffset}"));
    }
}
