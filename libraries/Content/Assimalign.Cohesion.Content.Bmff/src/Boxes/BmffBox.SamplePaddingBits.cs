using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;



namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Retains the sample padding bits box identity; typed payload reading and writing are unsupported.</summary>
[DebuggerDisplay("Bmff Box: Sample Padding Bits (padb)")]
public sealed class SamplePaddingBitsBox : BmffFullBox
{
    /// <summary>Creates a SamplePaddingBitsBox at its original location.</summary>
    /// <param name="offset">The absolute original header offset.</param>
    /// <param name="limit">The original total byte size.</param>
    public SamplePaddingBitsBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }




    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SamplePaddingBits;

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
