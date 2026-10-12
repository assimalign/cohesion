using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

internal sealed class EventThrottle
{
    private readonly object _sync = new();
    private long _lastEmission;
    private bool _emitted;
    private int _suppressed;

    internal bool TryEmit(out int suppressedCount)
    {
        lock (_sync)
        {
            long now = Stopwatch.GetTimestamp();
            if (_emitted && Stopwatch.GetElapsedTime(_lastEmission, now) < TimeSpan.FromSeconds(1))
            {
                if (_suppressed < int.MaxValue)
                {
                    _suppressed++;
                }
                suppressedCount = 0;
                return false;
            }

            _emitted = true;
            _lastEmission = now;
            suppressedCount = _suppressed;
            _suppressed = 0;
            return true;
        }
    }
}
