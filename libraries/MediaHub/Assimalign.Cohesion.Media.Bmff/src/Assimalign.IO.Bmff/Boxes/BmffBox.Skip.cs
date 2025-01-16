using Assimalign.IO.Bmff.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.IO.Bmff;

[DebuggerDisplay("Bmff Box: Skip (skip)")]
public sealed class SkipBox : BmffBoxComposite
{
    private IEnumerable<BmffBox> children;
    public SkipBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public override IEnumerable<BmffBox> Children => children;

    public override long Limit { get; }

    public override long Offset  {get;}

    public override BmffBoxType BoxType => BmffBoxType.Skip;

    public override void Read(BmffStream stream)
    {
        var boxes = new List<BmffBox>();
        var reader = new BmffReaderDefault(stream);

        while (reader.Read())
        {
            boxes.Add(reader.Current);
        }

        children = boxes;
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
