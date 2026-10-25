using System;
using System.Globalization;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Statement-owned retained execution memory; no shared atomics or process-heap samples.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphQueryMemory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryMemory.DebugView))]
internal sealed class GraphQueryMemory
{
    private GraphQueryMemoryPool? _pool;
    private GraphQueryMemoryBuffers? _buffers;
    private Dictionary<object, (int References, long Bytes)>? _values;
    private Stack<object?>? _pending;
    private List<object>? _journal;
    internal GraphQueryMemory(long limit, GraphQueryMemoryPool? pool = null)
    {
        Limit = limit;
        _pool = pool;
    }

    internal long Limit { get; }
    internal long RetainedBytes { get; private set; }
    internal long PeakRetainedBytes { get; private set; }
    internal int ActiveLeases { get; private set; }
    internal long RetainedBufferCapacity => (long)(_values?.EnsureCapacity(0) ?? 0) + (_pending?.EnsureCapacity(0) ?? 0) + (_journal?.Capacity ?? 0);

    internal void RetainValue(object? value, CancellationToken token)
    {
        var buffers = _buffers ??= _pool?.RentBuffers() ?? new();
        var values = _values ??= buffers.Values;
        var pending = _pending ??= buffers.Pending;
        var journal = _journal ??= buffers.Journal;
        long scalars = 0;
        pending.Push(value);
        try
        {
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                if (current is null) { continue; }
                if (!GraphValueMemory.IsTracked(current))
                {
                    long scalarBytes = GraphValueMemory.Shallow(current);
                    Add(scalarBytes); scalars += scalarBytes;
                    GraphValueMemory.PushChildren(current, pending);
                    continue;
                }
                if (values.TryGetValue(current, out var retained))
                { values[current] = (checked(retained.References + 1), retained.Bytes); journal.Add(current); continue; }
                // One combined grant covers the ledger entry and root-owner bookkeeping.
                long bytes = 96 + GraphValueMemory.Shallow(current);
                Add(bytes);
                values.Add(current, (1, bytes));
                journal.Add(current);
                GraphValueMemory.PushChildren(current, pending);
            }
        }
        catch
        {
            // Roll back only the successful increments, without walking descendants twice.
            for (int i = journal.Count - 1; i >= 0; i--)
            {
                var current = journal[i];
                var retained = values[current];
                if (retained.References == 1) { values.Remove(current); Release(retained.Bytes); }
                else { values[current] = (retained.References - 1, retained.Bytes); }
            }
            Release(scalars);
            throw;
        }
        finally { pending.Clear(); journal.Clear(); }
    }

    internal void ReleaseValue(object? value)
    {
        var values = _values!;
        var pending = _pending!;
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (current is null) { continue; }
            if (!GraphValueMemory.IsTracked(current))
            { Release(GraphValueMemory.Shallow(current)); GraphValueMemory.PushChildren(current, pending); continue; }
            var retained = values[current];
            if (retained.References > 1) { values[current] = (retained.References - 1, retained.Bytes); continue; }
            values.Remove(current);
            Release(retained.Bytes);
            GraphValueMemory.PushChildren(current, pending);
        }
    }

    internal GraphMemoryLease Reserve(long bytes)
    {
        Add(bytes);
        ActiveLeases++;
        return new(this, bytes);
    }

    internal Dictionary<object, int> RentOwner() => _pool?.RentOwner() ?? new(ReferenceEqualityComparer.Instance);
    internal void ReturnOwner(Dictionary<object, int> owner) => _pool?.ReturnOwner(owner);

    internal void Add(long bytes)
    {
        if (bytes < 0) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
        if (bytes > Limit - RetainedBytes)
        { throw new GraphPlanningException("CG05020", string.Format(CultureInfo.CurrentCulture, ErrorMessages.QueryMemoryBudgetExceeded, Limit)); }
        RetainedBytes += bytes;
        PeakRetainedBytes = Math.Max(PeakRetainedBytes, RetainedBytes);
    }

    internal void Release(long bytes)
    {
        if (bytes < 0 || bytes > RetainedBytes) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
        RetainedBytes -= bytes;
    }

    internal void EndLease(long bytes)
    {
        Release(bytes);
        ActiveLeases--;
        if (ActiveLeases == 0 && RetainedBytes == 0)
        {
            // LastStatementMetrics preserves totals, never the workspaces those totals describe.
            _values = null; _pending = null; _journal = null;
            var buffers = _buffers;
            _buffers = null;
            var pool = _pool;
            _pool = null;
            if (buffers is not null) { pool?.ReturnBuffers(buffers); }
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphQueryMemory value)
        {
            _ = value;
        }
    }
}
