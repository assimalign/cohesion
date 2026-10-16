using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The MediaHeader box; ISO/IEC 14496-12 8.4.2.</summary>
public sealed class MediaHeaderBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a MediaHeaderBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public MediaHeaderBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.MediaHeader;

    /// <summary>Gets or sets creation time in whole UTC seconds since 1904-01-01.</summary>
    public ulong CreationTime { get; set; }
    /// <summary>Gets or sets modification time in whole UTC seconds since 1904-01-01.</summary>
    public ulong ModificationTime { get; set; }
    /// <summary>Gets or sets the nonzero time units per second.</summary>
    public uint TimeScale { get; set; } = 1000;
    /// <summary>Gets or sets the duration in timescale units, including the all-ones unknown value.</summary>
    public ulong Duration { get; set; }
    /// <summary>Gets or sets the packed ISO-639-2/T language and retained high padding bit.</summary>
    public ushort PackedLanguage { get; set; } = 0x55c4;
    /// <summary>Gets the three decoded ISO-639-2/T letters; set PackedLanguage to edit the wire representation.</summary>
    public string Language => BmffFields.Language(PackedLanguage);
    /// <summary>Gets or sets the predefined word.</summary>
    public ushort PreDefined { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);
        CreationTime = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        ModificationTime = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        TimeScale = BmffFields.U32(stream);
        Duration = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        PackedLanguage = BmffFields.U16(stream);
        PreDefined = BmffFields.U16(stream);
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
        BmffFields.U32(stream, TimeScale);
        BmffFields.Sized(stream, Duration, Version == 1 ? 8 : 4);
        BmffFields.U16(stream, PackedLanguage);
        BmffFields.U16(stream, PreDefined);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return (Version == 1 ? 36L : 24L) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        BmffFields.Width(CreationTime, Version == 1 ? 8 : 4, Offset);
        BmffFields.Width(ModificationTime, Version == 1 ? 8 : 4, Offset);
        BmffFields.Width(Duration, Version == 1 ? 8 : 4, Offset);
        if (TimeScale == 0)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
