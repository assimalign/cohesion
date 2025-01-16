using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.IO.Bmff;


[DebuggerDisplay("Bmff Box: Handler (hdlr)")]
public sealed class HandlerBox : BmffBox
{
    public HandlerBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public override bool IsLeaf => true;

    public override bool IsComposite => false;

    public override long Limit { get; }

    public override long Offset { get; }

    public override BmffBoxType BoxType => BmffBoxType.Handler;

    public override void Read(BmffStream stream)
    {

    }

    public override void Write(BmffStream stream)
    {

    }
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }

}
