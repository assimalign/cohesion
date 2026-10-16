using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

[DebuggerDisplay("Bmff Box: Chunk Offset (stco)")]
public sealed class ChunkOffsetBox : BmffBox
{
    private uint[] _entries;

    public ChunkOffsetBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }

    public uint[] Entries => _entries;
    public override bool IsLeaf => true;
    public override bool IsComposite => false;
    public override long Limit { get; }
    public override long Offset { get; }
    public override BmffBoxType BoxType => BmffBoxType.ChunkOffset;

    public override void Read(BmffStream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        var count = BmffBinary.ReadField(stream, buffer[..4]).ReadUInt32BigEndian();

        _entries = new uint[count];

        for (uint i = 0; i < count; i++)
        {
            _entries[i] = BmffBinary.ReadField(stream, buffer[..4]).ReadUInt32BigEndian();
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
}
