using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.IO.Bmff;


[DebuggerDisplay("Bmff Box: File Delivery Item Information (segr)")]
public sealed class FileDeliverySessionGroupBox : BmffBox
{
    public FileDeliverySessionGroupBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    public override bool IsLeaf => throw new NotImplementedException();

    public override bool IsComposite => throw new NotImplementedException();

    public override long Limit { get;}

    public override long Offset { get; }

    public override BmffBoxType BoxType => BmffBoxType.FileDeliverySessionGroup;

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
