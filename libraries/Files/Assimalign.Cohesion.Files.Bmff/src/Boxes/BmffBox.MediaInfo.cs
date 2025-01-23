
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Files.Bmff.Internal;

namespace Assimalign.Cohesion.Files.Bmff;

[DebuggerDisplay("Bmff Box: Media Information (minf)")]
public sealed class MediaInfoBox : BmffBoxComposite
{
    private IList<BmffBox> children = new List<BmffBox>();

    public MediaInfoBox(long offset)
    {
        this.Offset = offset;
    }
    public MediaInfoBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public override long Limit { get; }
    public override long Offset { get; }
    public override BmffBoxType BoxType => BmffBoxType.MediaInfo;
    public override IEnumerable<BmffBox> Children => this.children;

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
