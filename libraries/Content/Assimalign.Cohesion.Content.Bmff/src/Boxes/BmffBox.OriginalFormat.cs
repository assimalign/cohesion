using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The OriginalFormat box; ISO/IEC 14496-12 8.12.2.</summary>
public sealed class OriginalFormatBox : BmffBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a OriginalFormatBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public OriginalFormatBox(long offset, long limit) { Offset = offset; Limit = limit;  }

    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.OriginalFormat;

    /// <summary>Gets or sets the original sample entry FourCC.</summary>
    public FourCC DataFormat { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        DataFormat = FourCC.FromUInt32(BmffFields.U32(stream));
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();

        BmffFields.U32(stream, DataFormat.Value);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 4L + _trailing.Length;
    }
    private void Validate()
    {
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
