﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;

[DebuggerDisplay("Bmff Box: Time To Sample [Decoding] (stts)")]
public sealed class SampleDecodingTimeBox : BmffBox
{
    public SampleDecodingTimeBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public override bool IsLeaf => true;
    public override bool IsComposite => false;
    public override long Limit { get; }
    public override long Offset { get; }
    public override BmffBoxType BoxType => BmffBoxType.SampleDecodingTime;

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
