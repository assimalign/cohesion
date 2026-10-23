using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One session's bounded cache of empty accounting buffers; execution has one owner.</summary>
internal sealed class GraphQueryMemoryPool : IDisposable
{
    private GraphQueryMemoryBuffers? _buffers;
    private readonly Dictionary<object, int>?[] _owners = new Dictionary<object, int>?[8];
    private volatile bool _disposed;
    internal bool HasCachedValueBuffers => _buffers is not null;

    internal GraphQueryMemoryBuffers RentBuffers()
    {
        var buffers = _buffers;
        _buffers = null;
        return buffers ?? new();
    }

    internal Dictionary<object, int> RentOwner()
    {
        for (int i = 0; i < _owners.Length; i++)
        {
            var owner = _owners[i];
            _owners[i] = null;
            if (owner is not null) { return owner; }
        }
        return new(ReferenceEqualityComparer.Instance);
    }

    internal void ReturnOwner(Dictionary<object, int> owner)
    {
        owner.Clear();
        if (_disposed || owner.EnsureCapacity(0) > 128) { return; }
        for (int i = 0; i < _owners.Length; i++)
        {
            if (_owners[i] is not null) { continue; }
            _owners[i] = owner;
            if (_disposed) { _owners[i] = null; }
            return;
        }
    }

    internal void ReturnBuffers(GraphQueryMemoryBuffers buffers)
    {
        buffers.Values.Clear(); buffers.Pending.Clear(); buffers.Journal.Clear();
        if (_disposed || buffers.Values.EnsureCapacity(0) > 1024 || buffers.Pending.EnsureCapacity(0) > 256 || buffers.Journal.Capacity > 256) { return; }
        _buffers = buffers;
        if (_disposed) { _buffers = null; }
    }

    internal long CachedCapacity
    {
        get
        {
            long capacity = (_buffers?.Values.EnsureCapacity(0) ?? 0) + (_buffers?.Pending.EnsureCapacity(0) ?? 0) + (_buffers?.Journal.Capacity ?? 0);
            foreach (var owner in _owners) { capacity += owner?.EnsureCapacity(0) ?? 0; }
            return capacity;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        // Drop references only: an active execution may already have taken a buffer into a
        // local. Dispose never mutates rented collections or races their enumerators.
        _buffers = null;
        Array.Clear(_owners);
    }
}
