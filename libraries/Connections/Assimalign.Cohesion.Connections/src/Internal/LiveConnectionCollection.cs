using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Internal;

internal sealed class LiveConnectionCollection<TConnection> : IReadOnlyCollection<TConnection> where TConnection : class, IConnection
{
    private readonly ConcurrentDictionary<TConnection, byte> _connections = new(ReferenceEqualityComparer.Instance);
    private readonly Action<object?> _remove;
    private int _count;

    public LiveConnectionCollection()
    {
        _remove = Remove;
    }

    public int Count => Volatile.Read(ref _count);

    public void Track(TConnection connection)
    {
        if (_connections.TryAdd(connection, 0))
        {
            Interlocked.Increment(ref _count);
            // Register only after adding: an already-closed connection invokes removal immediately.
            // Cancellation releases its registration; no per-connection closure or wrapper is needed.
            connection.ConnectionClosed.UnsafeRegister(_remove, connection);
        }
    }

    private void Remove(object? state)
    {
        if (_connections.TryRemove((TConnection)state!, out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    public IEnumerator<TConnection> GetEnumerator()
    {
        foreach (KeyValuePair<TConnection, byte> entry in _connections)
        {
            yield return entry.Key;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
