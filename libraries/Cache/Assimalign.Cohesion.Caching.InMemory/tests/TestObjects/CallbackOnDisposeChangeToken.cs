using System;
using System.Threading;

namespace Assimalign.Cohesion.Caching.InMemory.Tests.TestObjects;

internal sealed class CallbackOnDisposeChangeToken : IChangeToken
{
    public int Notifications { get; private set; }

    public IDisposable OnChange(Action<object?> callback, object? state)
        => new Subscription(this, callback, state);

    private sealed class Subscription : IDisposable
    {
        private readonly CallbackOnDisposeChangeToken _owner;
        private readonly Action<object?> _callback;
        private readonly object? _state;

        public Subscription(CallbackOnDisposeChangeToken owner, Action<object?> callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public void Dispose()
        {
            _owner.Notifications++;
            _callback(_state);
        }
    }
}
