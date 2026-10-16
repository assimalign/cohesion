using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The imir box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ImageMirrorBox : BmffBox
{
    /// <summary>Creates an empty ImageMirrorBox model.</summary>
    public ImageMirrorBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x696d6972;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();
    /// <summary>Gets or sets the mirror axis (0 vertical, 1 horizontal).</summary>
    public byte Axis { get; set; }
    /// <summary>Gets or sets the preserved high reserved bits.</summary>
    public byte Reserved { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        var value = HeifFields.Read(stream, 1);
        Axis = (byte)(value & 1);
        Reserved = (byte)(value >> 1);
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        HeifFields.Write(stream, (uint)((Reserved << 1) | Axis), 1);
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { Validate(); return 1L + TrailingData.Length; }
    private void Validate()
    {
        if (Axis > 1 || Reserved > 127)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
}
