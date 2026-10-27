using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One operator owns a changing amount of retained storage until disposal or result transfer.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphMemoryOwner")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphMemoryOwner.DebugView))]
internal sealed class GraphMemoryOwner : IDisposable
{
    private GraphQueryMemory? _memory;
    private long _bytes;
    private Dictionary<object, int>? _values;

    internal GraphMemoryOwner(GraphQueryMemory memory)
    {
        _memory = memory;
        _ = memory.Reserve(0);
    }

    internal void Add(long bytes)
    {
        ObjectDisposedException.ThrowIf(_memory is null, this);
        _memory.Add(bytes);
        _bytes += bytes;
    }

    internal void Release(long bytes)
    {
        ObjectDisposedException.ThrowIf(_memory is null, this);
        if (bytes < 0 || bytes > _bytes) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
        _memory.Release(bytes);
        _bytes -= bytes;
    }

    internal void RetainValue(object? value, CancellationToken token)
    {
        if (value is null) { return; }
        if (!GraphValueMemory.IsTracked(value) && value is not (GraphNode or GraphRelationship)) { Add(GraphValueMemory.Shallow(value)); return; }
        ObjectDisposedException.ThrowIf(_memory is null, this);
        _values ??= _memory.RentOwner();
        _values.TryGetValue(value, out int count);
        // The statement ledger's entry grant includes root-owner bookkeeping. Charging
        // another entry here counted the same retained container's accounting twice.
        _memory.RetainValue(value, token);
        _values[value] = checked(count + 1);
    }

    internal void ReleaseValue(object? value)
    {
        if (value is null) { return; }
        if (!GraphValueMemory.IsTracked(value) && value is not (GraphNode or GraphRelationship)) { Release(GraphValueMemory.Shallow(value)); return; }
        ObjectDisposedException.ThrowIf(_memory is null, this);
        int count = _values![value];
        if (count == 1) { _values.Remove(value); }
        else { _values[value] = count - 1; }
        _memory.ReleaseValue(value);
    }

    public void Dispose()
    {
        var memory = _memory;
        if (memory is not null && _values is not null)
        {
            foreach (var pair in _values)
            { for (int i = 0; i < pair.Value; i++) { memory.ReleaseValue(pair.Key); } }
            _values.Clear();
            memory.ReturnOwner(_values);
            _values = null;
        }
        _memory = null;
        memory?.EndLease(_bytes);
        _bytes = 0;
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphMemoryOwner value)
        {
            _ = value;
        }
    }
}
