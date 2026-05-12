using System;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;

internal readonly struct HPackHeaderField
{
    public const int RfcOverhead = 32;

    public HPackHeaderField(int? staticTableIndex, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        StaticTableIndex = staticTableIndex;
        Debug.Assert(name.Length > 0);
        Name = name.ToArray();
        Value = value.ToArray();
    }

    public int? StaticTableIndex { get; }

    public byte[] Name { get; }

    public byte[] Value { get; }

    public int Length => GetLength(Name.Length, Value.Length);

    public static int GetLength(int nameLength, int valueLength) => nameLength + valueLength + RfcOverhead;

    public override string ToString()
    {
        return Name is not null
            ? Encoding.Latin1.GetString(Name) + ": " + Encoding.Latin1.GetString(Value)
            : "<empty>";
    }
}
