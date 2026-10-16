using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;



namespace Assimalign.Cohesion.Content.Bmff;


/// <summary>Retains the group id to name box identity; typed payload reading and writing are unsupported.</summary>
public sealed class GroupIdToNameBox : BmffBox
{
    /// <summary>Creates a GroupIdToNameBox at its original location.</summary>
    /// <param name="offset">The absolute original header offset.</param>
    /// <param name="limit">The original total byte size.</param>
    public GroupIdToNameBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }





    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.GroupIdToName;

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
