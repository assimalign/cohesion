using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Caching.InMemory.Tests;

/// <summary>
/// Cohesion-native <see cref="IChangeToken"/> stub that lets a test fire <see cref="Notify"/>
/// when it wants to invalidate a cache entry.
/// </summary>
internal sealed class ManualChangeToken : IChangeToken
{
    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscribers = [];

    public int ActiveSubscribers
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        var subscription = new Subscription(this, callback, state);
        lock (_lock)
        {
            _subscribers.Add(subscription);
        }
        return subscription;
    }

    public void Notify()
    {
        Subscription[] snapshot;
        lock (_lock)
        {
            snapshot = _subscribers.ToArray();
        }

        foreach (var subscription in snapshot)
        {
            subscription.Notify();
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_lock)
        {
            _subscribers.Remove(subscription);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ManualChangeToken _owner;
        private readonly Action<object?> _callback;
        private readonly object? _state;
        private int _disposed;

        public Subscription(ManualChangeToken owner, Action<object?> callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public void Notify()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _callback(_state);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unsubscribe(this);
            }
        }
    }
}
