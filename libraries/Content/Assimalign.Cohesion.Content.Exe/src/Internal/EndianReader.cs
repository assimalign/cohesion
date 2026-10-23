using System;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Exe.Internal;

[global::System.Diagnostics.DebuggerDisplay("EndianReader")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal ref struct EndianReader
{
    private BinaryContentReader _reader;
    private readonly bool _bigEndian;

    internal EndianReader(ReadOnlySpan<byte> bytes, long origin, bool bigEndian)
    {
        _reader = new BinaryContentReader(bytes, origin);
        _bigEndian = bigEndian;
    }

    internal ushort UInt16() => _bigEndian ? _reader.ReadUInt16BigEndian() : _reader.ReadUInt16LittleEndian();
    internal uint UInt32() => _bigEndian ? _reader.ReadUInt32BigEndian() : _reader.ReadUInt32LittleEndian();
    internal ulong UInt64() => _bigEndian ? _reader.ReadUInt64BigEndian() : _reader.ReadUInt64LittleEndian();
    internal ulong Word(bool is64) => is64 ? UInt64() : UInt32();
    internal ReadOnlySpan<byte> Bytes(int count) => _reader.ReadBytes(count);

    private sealed class DebugView
    {
        public DebugView(EndianReader value)
        {
        }
    }
}
