using System;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.IO.Bmff;

using Assimalign.IO.Bmff.Internal;

[DebuggerDisplay("Bmff Box: Movie (moov)")]
public sealed class MovieBox : BmffBoxComposite
{
    public MovieBox(long offset)
    {
        this.Offset = offset;
    }
    public MovieBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public override long Limit { get; }
    public override long Offset { get; }
    public override BmffBoxType BoxType => BmffBoxType.Movie;
    public override IEnumerable<BmffBox> Children { get; } = new List<BmffBox>();

    public override void Read(BmffStream stream)
    {
        var reader  = new BmffReaderDefault(stream);

        while (reader.Read())
        {
            ((List<BmffBox>)Children).Add(reader.Current);
        }
    }

    public override void Write(BmffStream stream)
    {
        
    }

    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }

    public override string ToString() => "Bmff Box: Movie (moov)";
}
