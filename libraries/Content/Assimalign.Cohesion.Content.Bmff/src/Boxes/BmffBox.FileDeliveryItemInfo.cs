using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Retains the file delivery item info box identity; typed payload reading and writing are unsupported.</summary>
public sealed class FileDeliveryItemInfoBox : BmffBoxComposite
{
    private readonly IList<BmffBox> _children = new List<BmffBox>();

    /// <summary>Creates a FileDeliveryItemInfoBox at its original location.</summary>
    /// <param name="offset">The absolute original header offset.</param>
    /// <param name="limit">The original total byte size.</param>
    public FileDeliveryItemInfoBox(long offset, long limit)
    {
        SetOriginalLocation(offset, limit);
    }
    /// <inheritdoc/>
    public override IList<BmffBox> Children => _children;



    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.FileDeliveryItemInfo;

    internal override void WritePrefix(BmffStream stream) =>
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);

    /// <inheritdoc/>
    public override long GetPayloadSize() =>
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);

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
