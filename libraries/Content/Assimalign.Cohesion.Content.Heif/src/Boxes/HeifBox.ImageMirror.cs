using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The imir box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class ImageMirrorBox : BmffBox
{
    /// <summary>Creates an empty ImageMirrorBox model.</summary>
    public ImageMirrorBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x696d6972;
    internal HeifReaderOptions Options { get; set; } = new();
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
    /// <inheritdoc/>
    protected override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" axis={Axis}"));
    }

    private sealed class DebugView
    {
        private readonly ImageMirrorBox _target;

        public DebugView(ImageMirrorBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public byte[] TrailingData => _target.TrailingData;
        public byte Axis => _target.Axis;
        public byte Reserved => _target.Reserved;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
