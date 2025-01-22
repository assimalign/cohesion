using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;

[DebuggerDisplay("Bmff Box: Movie Fragment Random Access Offset (mfro)")]
public sealed class MovieFragmentRandomAccessOffsetBox : BmffBox
{
    public MovieFragmentRandomAccessOffsetBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    public override bool IsLeaf => throw new NotImplementedException();

    public override bool IsComposite => throw new NotImplementedException();

    public override long Limit { get; }

    public override long Offset { get; }

    public override BmffBoxType BoxType => BmffBoxType.MovieFragmentRandomAccessOffset;

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
