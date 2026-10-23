using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The pixi box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class PixelInformationBox : BmffFullBox
{
    /// <summary>Creates an empty PixelInformationBox model.</summary>
    public PixelInformationBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x70697869;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();
    /// <summary>Gets or sets the ordered channel depths.</summary>
    public byte[] BitsPerChannel { get; set; } = Array.Empty<byte>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        HeifFields.Version(Version, Flags, 0, 0, stream.Offset);
        BitsPerChannel = HeifFields.Bytes(stream, HeifFields.Read(stream, 1), Options.MaxBufferedPayloadSize);
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        HeifFields.Write(stream, (uint)BitsPerChannel.Length, 1);
        stream.Write(BitsPerChannel);
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { Validate(); return 5L + BitsPerChannel.Length + TrailingData.Length; }
    private void Validate()
    {
        HeifFields.Version(Version, Flags, 0, 0, Offset);
        if (BitsPerChannel.Length > 255)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
    /// <inheritdoc/>
    protected override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" channels={BitsPerChannel.Length}"));
    }

    private sealed class DebugView
    {
        private readonly PixelInformationBox _target;

        public DebugView(PixelInformationBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public byte[] TrailingData => _target.TrailingData;
        public byte[] BitsPerChannel => _target.BitsPerChannel;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
