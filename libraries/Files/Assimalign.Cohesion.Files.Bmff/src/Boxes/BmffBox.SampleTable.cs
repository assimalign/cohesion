using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;

using Assimalign.Cohesion.Files.Bmff.Internal;

[DebuggerDisplay("Bmff Box: Sample Table (stbl)")]
public sealed class SampleTableBox : BmffBoxComposite
{
    private IList<BmffBox> children = new List<BmffBox>();

    public SampleTableBox(long offset)
    {
        this.Offset = offset;
    }
    public SampleTableBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public override long Limit { get; }
    public override long Offset { get; }
    public override BmffBoxType BoxType => BmffBoxType.SampleTable;
    public override IEnumerable<BmffBox> Children => this.children;

    public override void Read(BmffStream stream)
    {
        var bmffBoxes = new List<BmffBox>();
        var bmffReader = new BmffReaderDefault(stream);

        while (bmffReader.Read())
        {
            bmffBoxes.Add(bmffReader.Current);
        }

        children = bmffBoxes;
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
