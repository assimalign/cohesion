using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Buffers.Binary.BinaryPrimitives;

using Assimalign.Cohesion.Content.Media;
using Assimalign.IO;

namespace Assimalign.Cohesion.Files.Bmff;

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
        var count = ReadUInt32BigEndian(stream.ReadBytes(4));

        _entries = new uint[count];

        for (uint i = 0; i < count; i++)
        {
            _entries[i] = ReadUInt32BigEndian(stream.ReadBytes(4));
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
