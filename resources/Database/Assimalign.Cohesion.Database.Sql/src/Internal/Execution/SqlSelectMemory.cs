using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// One admitting buffer owns this map. Byte-only transfers never carry its references.
[global::System.Diagnostics.DebuggerDisplay("SqlSelectMemory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlSelectMemory.DebugView))]
internal struct SqlSelectMemory
{
    internal const long WideValueThreshold = 256;
    internal const long WideValueEntryBytes = 64;

    internal long Bytes;
    private Dictionary<object, int>? _wideCells;

    // A narrow owner never creates a map.
    internal readonly bool HasWideCells => _wideCells is not null;

    internal readonly long ValueBytes(object? value, out bool wide)
    {
        long bytes = SqlHashJoinTable.ValueBytes(value);
        wide = bytes >= WideValueThreshold;
        return !wide ? bytes : _wideCells is not null && _wideCells.ContainsKey(value!) ? 0 : bytes + WideValueEntryBytes;
    }

    internal void AddValue(object value)
    {
        _wideCells ??= new(ReferenceEqualityComparer.Instance);
        _wideCells.TryGetValue(value, out int count);
        _wideCells[value] = count + 1;
    }

    internal readonly long LastValueBytes(object value)
        => _wideCells![value] == 1 ? SqlHashJoinTable.ValueBytes(value) + WideValueEntryBytes : 0;

    internal void RemoveValue(object value)
    {
        int count = _wideCells![value];
        if (count == 1) { _wideCells.Remove(value); }
        else { _wideCells[value] = count - 1; }
    }

    internal static long VectorBytes(object?[] values)
    {
        SqlSelectMemory local = default;
        long bytes = 32L + 24L + 8L * values.Length;
        foreach (object? value in values)
        {
            bytes += local.ValueBytes(value, out bool wide);
            if (wide) { local.AddValue(value!); }
        }
        return bytes;
    }

    internal readonly long RowBytes(object?[] row, out bool wide, long overhead = 32)
        => overhead + AdmissionArrayBytes(row, [], out wide);

    internal readonly long EntryBytes(object?[] output, object?[] keys, out bool wide, long overhead = 32)
    {
        long bytes = overhead + AdmissionArrayBytes(output, [], out bool outputWide)
            + AdmissionArrayBytes(keys, output, out bool keysWide);
        wide = outputWide || keysWide;
        return bytes;
    }

    // The collector always charges an ordering-key array, even an empty one.
    internal readonly long ReplacementBytes(object?[] output, object?[] keys, object?[] previousOutput, object?[] previousKeys, out bool wide)
    {
        long bytes = EntryBytes(output, keys, out wide, 0);
        for (int array = 0; array < 2; array++)
        {
            var removed = array == 0 ? previousOutput : previousKeys;
            bytes -= 24L + 8L * removed.Length;
            for (int i = 0; i < removed.Length; i++)
            {
                object? value = removed[i];
                long payload = SqlHashJoinTable.ValueBytes(value);
                if (payload < WideValueThreshold) { bytes -= payload; continue; }
                if (SeenBefore(value, removed, i) || array == 1 && Contains(previousOutput, value)
                    || Contains(output, value) || Contains(keys, value)) { continue; }
                if (_wideCells![value!] == Count(previousOutput, value) + Count(previousKeys, value))
                { bytes -= payload + WideValueEntryBytes; }
            }
        }
        return bytes;
    }

    internal void AddRow(object?[] row)
    {
        foreach (object? value in row)
        {
            if (SqlHashJoinTable.ValueBytes(value) < WideValueThreshold) { continue; }
            AddValue(value!);
        }
    }

    internal long RemoveRow(object?[] row, long overhead = 32)
    {
        long bytes = overhead + 24L + 8L * row.Length;
        foreach (object? value in row)
        {
            long payload = SqlHashJoinTable.ValueBytes(value);
            if (payload < WideValueThreshold) { bytes += payload; continue; }
            int count = _wideCells![value!];
            if (count == 1)
            {
                _wideCells.Remove(value!);
                bytes += payload + WideValueEntryBytes;
            }
            else { _wideCells[value!] = count - 1; }
        }
        // Keep the empty allocation for the next heap replacement, but no dead key.
        return bytes;
    }

    internal void Replace(object?[] output, object?[] keys, object?[] previousOutput, object?[] previousKeys)
    {
        RemoveRow(previousOutput, 0);
        RemoveRow(previousKeys, 0);
        AddRow(output);
        AddRow(keys);
    }

    internal void Clear() { Bytes = 0; _wideCells = null; }

    private readonly long AdmissionArrayBytes(object?[] row, object?[] earlier, out bool wide)
    {
        long bytes = 24L + 8L * row.Length;
        wide = false;
        for (int i = 0; i < row.Length; i++)
        {
            object? value = row[i];
            long payload = SqlHashJoinTable.ValueBytes(value);
            if (payload < WideValueThreshold) { bytes += payload; }
            else
            {
                wide = true;
                if (_wideCells is not null && _wideCells.ContainsKey(value!) || SeenBefore(value, row, i) || Contains(earlier, value)) { continue; }
                bytes += payload + WideValueEntryBytes;
            }
        }
        return bytes;
    }

    private static bool SeenBefore(object? value, object?[] row, int end)
    {
        for (int i = 0; i < end; i++) { if (ReferenceEquals(row[i], value)) { return true; } }
        return false;
    }

    private static bool Contains(object?[] row, object? value) => SeenBefore(value, row, row.Length);

    private static int Count(object?[] row, object? value)
    {
        int count = 0;
        foreach (object? cell in row) { if (ReferenceEquals(cell, value)) { count++; } }
        return count;
    }

    private sealed class DebugView
    {
        public DebugView(SqlSelectMemory value) { _ = value; }
    }
}
