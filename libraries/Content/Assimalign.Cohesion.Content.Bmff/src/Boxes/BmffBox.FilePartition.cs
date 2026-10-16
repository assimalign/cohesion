using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;



namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Retains the file partition box identity; typed payload reading and writing are unsupported.</summary>
public sealed class FilePartitionBox : BmffFullBox
{
    /// <summary>Creates a FilePartitionBox at its original location.</summary>
    /// <param name="offset">The absolute original header offset.</param>
    /// <param name="limit">The original total byte size.</param>
    public FilePartitionBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }




    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.FilePartition;

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
