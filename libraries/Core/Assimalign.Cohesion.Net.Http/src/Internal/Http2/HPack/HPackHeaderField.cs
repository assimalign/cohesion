﻿using System;
using System.Text;
using System.Diagnostics;


namespace Assimalign.Cohesion.Net.Http.Internal;

internal readonly struct HPackHeaderField
{
    // http://httpwg.org/specs/rfc7541.html#rfc.section.4.1
    public const int RfcOverhead = 32;

    public HPackHeaderField(int? staticTableIndex, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        // Store the static table index (if there is one) for the header field.
        // ASP.NET Core has a fast path that sets a header value using the static table index instead of the name.
        StaticTableIndex = staticTableIndex;

        Debug.Assert(name.Length > 0);

        // TODO: We're allocating here on every new table entry.
        // That means a poorly-behaved server could cause us to allocate repeatedly.
        // We should revisit our allocation strategy here so we don't need to allocate per entry
        // and we have a cap to how much allocation can happen per dynamic table
        // (without limiting the number of table entries a server can provide within the table size limit).
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
        if (Name != null)
        {
            return Encoding.Latin1.GetString(Name) + ": " + Encoding.Latin1.GetString(Value);
        }
        else
        {
            return "<empty>";
        }
    }
}