using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Configuration.Internal;

internal sealed class ConfigurationChangeToken : IChangeToken
{
    private readonly Lock _lock;
    private readonly List<Subscriber> _subscribers;

    public ConfigurationChangeToken()
    {
        _lock = new Lock();
        _subscribers = [];
    }

    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var subscriber = new Subscriber(this, callback, state);

        lock (_lock)
        {
            _subscribers.Add(subscriber);
        }

        return subscriber;
    }

    public void Notify()
    {
        Subscriber[] subscribers;

        lock (_lock)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            subscribers = [.. _subscribers];
        }

        for (int i = 0; i < subscribers.Length; i++)
        {
            subscribers[i].Notify();
        }
    }

    private void Unsubscribe(Subscriber subscriber)
    {
        lock (_lock)
        {
            _subscribers.Remove(subscriber);
        }
    }

    private sealed class Subscriber : IDisposable
    {
        private readonly ConfigurationChangeToken _owner;
        private readonly Action<object?> _onChange;
        private readonly object? _state;

        private int _disposed;

        public Subscriber(ConfigurationChangeToken owner, Action<object?> onChange, object? state)
        {
            _owner = owner;
            _onChange = onChange;
            _state = state;
        }

        public void Notify()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _onChange.Invoke(_state);
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
