﻿using Assimalign.IO.Bmff.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.IO.Bmff;


[DebuggerDisplay("Bmff Box: Movie Extension (mvex)")]
public sealed class MovieExtensionBox : BmffBoxComposite
{
    private IEnumerable<BmffBox> children;
    public MovieExtensionBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    public override IEnumerable<BmffBox> Children => children;

    public override long Limit { get;}

    public override long Offset { get; }

    public override BmffBoxType BoxType => BmffBoxType.MovieExtension;

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
