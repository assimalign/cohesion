using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The ispe box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class ImageSpatialExtentsBox : BmffFullBox
{
    /// <summary>Creates an empty ImageSpatialExtentsBox model.</summary>
    public ImageSpatialExtentsBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x69737065;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();
    /// <summary>Gets or sets the image width.</summary>
    public uint Width { get; set; }
    /// <summary>Gets or sets the image height.</summary>
    public uint Height { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        HeifFields.Version(Version, Flags, 0, 0, stream.Offset);
        Width = HeifFields.Read(stream, 4);
        Height = HeifFields.Read(stream, 4);
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        HeifFields.Write(stream, Width, 4);
        HeifFields.Write(stream, Height, 4);
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { Validate(); return 12L + TrailingData.Length; }
    private void Validate()
    {
        HeifFields.Version(Version, Flags, 0, 0, Offset);
        if (Width == 0 || Height == 0)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
    /// <inheritdoc/>
    protected override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" dimensions={Width}x{Height}"));
    }

    private sealed class DebugView
    {
        private readonly ImageSpatialExtentsBox _target;

        public DebugView(ImageSpatialExtentsBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public byte[] TrailingData => _target.TrailingData;
        public uint Width => _target.Width;
        public uint Height => _target.Height;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
