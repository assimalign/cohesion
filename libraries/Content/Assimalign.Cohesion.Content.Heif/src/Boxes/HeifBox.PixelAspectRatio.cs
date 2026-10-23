using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The pasp box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class PixelAspectRatioBox : BmffBox
{
    /// <summary>Creates an empty PixelAspectRatioBox model.</summary>
    public PixelAspectRatioBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x70617370;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();
    /// <summary>Gets or sets the horizontal spacing.</summary>
    public uint HorizontalSpacing { get; set; } = 1;
    /// <summary>Gets or sets the vertical spacing.</summary>
    public uint VerticalSpacing { get; set; } = 1;

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        HorizontalSpacing = HeifFields.Read(stream, 4);
        VerticalSpacing = HeifFields.Read(stream, 4);
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        HeifFields.Write(stream, HorizontalSpacing, 4);
        HeifFields.Write(stream, VerticalSpacing, 4);
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { Validate(); return 8L + TrailingData.Length; }
    private void Validate()
    {
        if (HorizontalSpacing == 0 || VerticalSpacing == 0)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
    /// <inheritdoc/>
    protected override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" spacing={HorizontalSpacing}:{VerticalSpacing}"));
    }

    private sealed class DebugView
    {
        private readonly PixelAspectRatioBox _target;

        public DebugView(PixelAspectRatioBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public byte[] TrailingData => _target.TrailingData;
        public uint HorizontalSpacing => _target.HorizontalSpacing;
        public uint VerticalSpacing => _target.VerticalSpacing;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
