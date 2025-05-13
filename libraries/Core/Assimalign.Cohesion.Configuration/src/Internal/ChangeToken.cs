using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Internal;

internal class ChangeToken : IChangeToken
{
    private List<Subscriber> _subscribers;

    public ChangeToken()
    {
        _subscribers = new List<Subscriber>();
    }
    public IDisposable OnChange<T>(Action<T> callback, T state)
    {
        var subscriber = new Subscriber<T>(_subscribers)
        {
            State = state,
            OnChange = callback
        };
        _subscribers.Add(subscriber);
        return subscriber;
    }

    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        return OnChange<object>(state =>
        {
            callback(state);
        }, state!);
    }

    partial class Subscriber : IDisposable
    {
        private readonly List<Subscriber> _subscribers;

        public Subscriber(List<Subscriber> subscribers)
        {
            _subscribers = subscribers;
        }
        public void Dispose()
        {
            _subscribers.Remove(this);
        }
    }

    partial class Subscriber<T> : Subscriber
    {
        public Subscriber(List<Subscriber> subscribers) 
            : base(subscribers) { }
        public required T State { get; init; }
        public required Action<T> OnChange { get; init; }
    }
}
