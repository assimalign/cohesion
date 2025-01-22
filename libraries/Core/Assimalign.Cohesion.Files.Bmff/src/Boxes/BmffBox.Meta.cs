
using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Files.Bmff;

using Assimalign.Cohesion.Files.Bmff.Internal;

[DebuggerDisplay("Bmff Box: Meta (meta)")]
public sealed class MetaBox : BmffBoxComposite
{
    private IEnumerable<BmffBox> children;

    public MetaBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public override IEnumerable<BmffBox> Children { get; } = new List<BmffBox>();
    public override long Limit { get; }
    public override long Offset { get; }
    public override BmffBoxType BoxType => BmffBoxType.Meta;

    public override void Read(BmffStream stream)
    {
        //stream.ReadBytes(1);
        //var reader = new BmffReaderDefault(stream);

        //while (reader.Read())
        //{
        //    ((List<BmffBox>)Children).Add(reader.Current);
        //}
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
