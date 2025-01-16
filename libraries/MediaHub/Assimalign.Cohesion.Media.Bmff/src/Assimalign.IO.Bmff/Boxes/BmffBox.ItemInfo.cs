using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.IO.Bmff;

[DebuggerDisplay("Bmff Box: Item Information (iinf)")]
public sealed class ItemInfoBox : BmffBox
{
    public ItemInfoBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    public override bool IsLeaf => throw new NotImplementedException();

    public override bool IsComposite => throw new NotImplementedException();

    public override long Limit { get; }

    public override long Offset { get; }

    public override BmffBoxType BoxType => BmffBoxType.ItemInfo;

    public override void Read(BmffStream stream)
    {
        
    }

    public override void Write(BmffStream stream)
    {
        throw new NotImplementedException();
    }
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
}
