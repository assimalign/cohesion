using System;
using System.Threading;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

internal sealed class TestResourceHostSignalSource : IResourceHostSignalSource
{
    private readonly Lock _lock = new();
    private Func<ResourceHostStopSignal, bool>? _callback;
    private int _subscriptionCount;

    internal int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

    internal bool Signal(ResourceHostStopSignal stopSignal)
    {
        Func<ResourceHostStopSignal, bool>? callback;

        lock (_lock)
        {
            callback = _callback;
        }

        InvalidOperationException.ThrowIf(callback is null, "No resource host is subscribed.");
        return callback(stopSignal);
    }

    public IDisposable Subscribe(Func<ResourceHostStopSignal, bool> callback, string? stopEventName)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Interlocked.Increment(ref _subscriptionCount);

        lock (_lock)
        {
            _callback = callback;
        }

        return new Subscription(this, callback);
    }

    private void Unsubscribe(Func<ResourceHostStopSignal, bool> callback)
    {
        lock (_lock)
        {
            if (_callback == callback)
            {
                _callback = null;
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private TestResourceHostSignalSource? _source;
        private Func<ResourceHostStopSignal, bool>? _callback;

        internal Subscription(
            TestResourceHostSignalSource source,
            Func<ResourceHostStopSignal, bool> callback)
        {
            _source = source;
            _callback = callback;
        }

        public void Dispose()
        {
            TestResourceHostSignalSource? source = Interlocked.Exchange(ref _source, null);
            Func<ResourceHostStopSignal, bool>? callback = Interlocked.Exchange(ref _callback, null);

            if (source is not null && callback is not null)
            {
                source.Unsubscribe(callback);
            }
        }
    }
}
