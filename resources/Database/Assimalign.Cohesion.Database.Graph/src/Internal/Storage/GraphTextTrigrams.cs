using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Ordinal code-point tokens; three 21-bit scalars fit in one unsigned integer.</summary>
internal static class GraphTextTrigrams
{
    internal static bool CanSeek(object? value)
    {
        if (value is not string text) { return false; }
        int count = 0;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            // Ordinal predicates can compare an isolated surrogate to part of a valid pair.
            // Such patterns need a scan, rather than replacement-character tokenization.
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done) { return false; }
            remaining = remaining[consumed..];
            count++;
        }
        return count >= 3;
    }

    internal static IEnumerable<IndexKey> Keys(string text, CancellationToken token, Action<long>? reserve = null, Action? visit = null)
    {
        reserve?.Invoke(128);
        var seen = new HashSet<ulong>();
        ulong rolling = 0;
        int count = 0;
        foreach (var scalar in text.EnumerateRunes())
        {
            token.ThrowIfCancellationRequested();
            visit?.Invoke();
            rolling = ((rolling << 21) | (uint)scalar.Value) & 0x7fffffffffffffffUL;
            count++;
            if (count < 3 || seen.Contains(rolling)) { continue; }
            reserve?.Invoke(256); // Set capacity, owned key, change buffer and transaction undo entry.
            seen.Add(rolling);
            yield return Key(3, rolling);
        }
        if (count < 3)
        {
            reserve?.Invoke(256);
            yield return Key((byte)count, rolling);
        }
    }

    private static IndexKey Key(byte length, ulong value)
    {
        var bytes = new byte[9];
        bytes[0] = length;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), value);
        return new IndexKey(bytes);
    }

    internal static bool Matches(object? actual, object? pattern, string operation) => actual is string text && pattern is string value && operation switch
    {
        "CONTAINS" => text.Contains(value, StringComparison.Ordinal),
        "STARTS WITH" => text.StartsWith(value, StringComparison.Ordinal),
        "ENDS WITH" => text.EndsWith(value, StringComparison.Ordinal),
        _ => string.Equals(text, value, StringComparison.Ordinal),
    };
}
