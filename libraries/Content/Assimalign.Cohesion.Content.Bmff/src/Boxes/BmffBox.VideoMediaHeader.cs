using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The VideoMediaHeader box; ISO/IEC 14496-12 8.4.5.2.</summary>
public sealed class VideoMediaHeaderBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a VideoMediaHeaderBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public VideoMediaHeaderBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.VideoMediaHeader;

    /// <summary>Gets or sets the graphics mode.</summary>
    public ushort GraphicsMode { get; set; }
    /// <summary>Gets or sets the first operation color component.</summary>
    public ushort OpColorRed { get; set; }
    /// <summary>Gets or sets the second operation color component.</summary>
    public ushort OpColorGreen { get; set; }
    /// <summary>Gets or sets the third operation color component.</summary>
    public ushort OpColorBlue { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 1, stream.Offset);
        GraphicsMode = BmffFields.U16(stream);
        OpColorRed = BmffFields.U16(stream);
        OpColorGreen = BmffFields.U16(stream);
        OpColorBlue = BmffFields.U16(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U16(stream, GraphicsMode);
        BmffFields.U16(stream, OpColorRed);
        BmffFields.U16(stream, OpColorGreen);
        BmffFields.U16(stream, OpColorBlue);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 12L + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 1, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" graphics-mode={GraphicsMode}"));
    }
}
