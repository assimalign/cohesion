using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;



namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Retains the ipmp control box identity; typed payload reading and writing are unsupported.</summary>
[DebuggerDisplay("Bmff Box: IPMP Control (ipmc)")]
public sealed class IpmpControlBox : BmffFullBox
{
    /// <summary>Creates a IpmpControlBox at its original location.</summary>
    /// <param name="offset">The absolute original header offset.</param>
    /// <param name="limit">The original total byte size.</param>
    public IpmpControlBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    /// <inheritdoc/>
    public override bool IsLeaf => true;

    /// <inheritdoc/>
    public override bool IsComposite => false;

    /// <inheritdoc/>
    public override long Limit { get;}

    /// <inheritdoc/>
    public override long Offset { get; }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.IpmpControl;

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
    }

    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
