﻿using Assimalign.Cohesion.Files.Bmff.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;

public sealed class ProtectionSchemeInfoBox : BmffBoxComposite
{
    private IEnumerable<BmffBox> children;
    public ProtectionSchemeInfoBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }
    public override IEnumerable<BmffBox> Children => children;

    public override long Limit { get; }

    public override long Offset { get; }

    public override BmffBoxType BoxType => BmffBoxType.ProtectionSchemeInfo;

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
