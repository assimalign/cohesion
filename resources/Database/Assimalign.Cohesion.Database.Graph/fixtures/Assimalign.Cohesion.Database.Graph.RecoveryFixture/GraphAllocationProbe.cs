using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.RecoveryFixture;

internal sealed class GraphAllocationProbe
{
    private readonly long[] _bytes = new long[128];
    private readonly string?[] _names = new string?[128];
    private readonly int[] _threads = new int[128];
    private readonly long[] _preImages = new long[128];
    private readonly int[] _touched = new int[128];
    private readonly Assimalign.Cohesion.Database.Storage.Storage _storage;
    private readonly long[] _samples = new long[500];
    private readonly long _baseline;
    private int _count;
    private int _statement;
    private int _thread;
    private long _before;
    private bool _active;
    private int _sampleCount;
    internal GraphAllocationProbe(GraphDatabase database, bool textIndexed)
    {
        _baseline = textIndexed ? 54136 : 53264;
        _storage = DataStorage(database);
        SetCommitting(_storage, _ => Record("committing"));
        PageWrite(_storage) = _ => Record("page-write");
        var store = Store(database);
        SetLabelRead(store, () => Record("label-read"));
        SetLabelChanged(store, () => Record("label-changed"));
        SetDefinitionRead(store, () => Record("definition-read"));
        SetApplied(store, () => Record("applied"));
    }
    internal void Begin(long before, bool active)
    {
        _count = 0; _before = before; _active = active;
        _statement++; _thread = Environment.CurrentManagedThreadId;
    }
    internal void End(long bytes, bool minimum)
    {
        if (!_active) { return; }
        _active = false;
        _samples[_sampleCount++] = bytes;
        if (!minimum && bytes != _baseline && bytes != _baseline + 248 && _statement != 1620) { return; }
        Console.Write($"DELETE_STEPS|statement={_statement}|thread={_thread}|total={bytes}");
        for (int i = 0; i < _count; i++) { Console.Write($"|{_names[i]}={_bytes[i]}@{_threads[i]}:preimage={_preImages[i]}:touched={_touched[i]}"); }
        Console.WriteLine();
    }
    internal void Complete()
    {
        Array.Sort(_samples, 0, _sampleCount);
        for (int first = 0; first < _sampleCount;)
        {
            int last = first + 1;
            while (last < _sampleCount && _samples[last] == _samples[first]) { last++; }
            Console.WriteLine($"DELETE_HISTOGRAM|bytes={_samples[first]}|count={last - first}");
            first = last;
        }
    }
    private void Record(string name)
    {
        if (!_active || _count == _bytes.Length) { return; }
        _bytes[_count] = GC.GetAllocatedBytesForCurrentThread() - _before;
        _threads[_count] = Environment.CurrentManagedThreadId;
        long preImages = 0;
        foreach (var bracket in Brackets(_storage)) { preImages += PreImageBytes(bracket.Key); }
        _preImages[_count] = preImages;
        _touched[_count] = WriteLocks(_storage).Count;
        _names[_count++] = name;
    }
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Store")]
    private static extern GraphStore Store(GraphDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DataStorage")]
    private static extern GraphStorage DataStorage(GraphDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_activeBrackets")]
    private static extern ref Dictionary<StorageTransaction, (string Class, long Owner)> Brackets(Assimalign.Cohesion.Database.Storage.Storage storage);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_pageWriteLocks")]
    private static extern ref Dictionary<long, StorageTransaction> WriteLocks(Assimalign.Cohesion.Database.Storage.Storage storage);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PreImageBytes")]
    private static extern long PreImageBytes(StorageTransaction transaction);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_TransactionCommitting")]
    private static extern void SetCommitting(Assimalign.Cohesion.Database.Storage.Storage storage, Action<StorageTransaction>? action);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "PagePinnedForWrite")]
    private static extern ref Action<PageId>? PageWrite(Assimalign.Cohesion.Database.Storage.Storage storage);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_OnLabelEntryRead")]
    private static extern void SetLabelRead(GraphStore store, Action? value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_OnLabelIndexChanged")]
    private static extern void SetLabelChanged(GraphStore store, Action? value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_OnIndexDefinitionRead")]
    private static extern void SetDefinitionRead(GraphStore store, Action? value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_AfterApply")]
    private static extern void SetApplied(GraphStore store, Action? value);
}
