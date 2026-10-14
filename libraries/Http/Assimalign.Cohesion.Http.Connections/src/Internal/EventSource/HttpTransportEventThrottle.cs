using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>One process-wide emission per second for each repeatable operator event.</summary>
internal sealed class HttpTransportEventThrottle
{
    private readonly Lock _gate = new();
    private long _lastTimestamp;
    private bool _emitted;
    private int _suppressed;

    public bool TryAcquire(out int suppressedCount)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_emitted && now - _lastTimestamp < Stopwatch.Frequency)
            {
                // Saturate the bounded payload; occurrence counters are independently exact.
                if (_suppressed < int.MaxValue)
                {
                    _suppressed++;
                }
                suppressedCount = 0;
                return false;
            }
            suppressedCount = _suppressed;
            _suppressed = 0;
            _lastTimestamp = now;
            _emitted = true;
            return true;
        }
    }
}
