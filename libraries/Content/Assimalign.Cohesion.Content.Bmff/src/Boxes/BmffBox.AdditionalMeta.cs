using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Files.Bmff;

using Assimalign.Cohesion.Files.Bmff.Internal;


[DebuggerDisplay("Bmff Box: Additional Metadata (meco)")]
public sealed class AdditionalMetaBox : BmffBoxComposite
{
    public AdditionalMetaBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    public override IEnumerable<BmffBox> Children { get; } = new List<BmffBox>();
    public override long Limit { get; }
    public override long Offset { get; }
    public override BmffBoxType BoxType => BmffBoxType.AdditionalMeta;

    public override void Read(BmffStream stream)
    {
        var reader = new BmffReaderDefault(stream);

        while (reader.Read())
        {
            ((List<BmffBox>)Children).Add(reader.Current);
        }
    }

    public override void Write(BmffStream stream)
    {
        throw new NotImplementedException();
    }

    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }

    public override string ToString() => "Bmff Box: Additional Metadata (meco)";
}
