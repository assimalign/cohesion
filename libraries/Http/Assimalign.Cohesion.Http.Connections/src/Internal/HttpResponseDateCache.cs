using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal;

// A process-wide cache in production; provider-keyed caches keep deterministic clocks isolated.
internal sealed class HttpResponseDateCache
{
    private static readonly ConditionalWeakTable<TimeProvider, HttpResponseDateCache> _providers = new();
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private HttpResponseDateHeader? _current;

    public HttpResponseDateCache(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public static HttpResponseDateCache For(TimeProvider timeProvider)
        => _providers.GetValue(timeProvider, static provider => new HttpResponseDateCache(provider));

    public HttpResponseDateHeader GetCurrent()
    {
        long second = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        HttpResponseDateHeader? current = Volatile.Read(ref _current);
        if (current?.UtcSecond == second)
        {
            return current;
        }

        lock (_gate)
        {
            // Re-read under the lock so simultaneous refreshes neither format twice nor publish
            // an earlier caller's stale timestamp. Inequality also handles clock rollback.
            second = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            current = _current;
            if (current?.UtcSecond != second)
            {
                current = new HttpResponseDateHeader(second, HttpDate.Format(DateTimeOffset.FromUnixTimeSeconds(second)));
                Volatile.Write(ref _current, current);
            }
            return current;
        }
    }
}
