using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>Limits parse-failure reports across every session of the process-wide event source.</summary>
[global::System.Diagnostics.DebuggerDisplay("KeyValueParseFailureThrottle")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueParseFailureThrottle.DebugView))]
internal sealed class KeyValueParseFailureThrottle
{
    private readonly Lock _gate = new();
    private bool _hasReported;
    private long _lastReportTimestamp;
    private int _suppressed;

    // TcpAcceptBackoff.TryReport uses the same timestamp boundary and saturating payload count.
    // The source separately maintains an exact Interlocked total, including disabled writes.
    public bool TryReport(long timestamp, out int suppressedCount)
    {
        lock (_gate)
        {
            if (_hasReported && Stopwatch.GetElapsedTime(_lastReportTimestamp, timestamp).TotalSeconds < 1)
            {
                _suppressed = _suppressed == int.MaxValue ? int.MaxValue : _suppressed + 1;
                suppressedCount = 0;
                return false;
            }

            _hasReported = true;
            _lastReportTimestamp = timestamp;
            suppressedCount = _suppressed;
            _suppressed = 0;
            return true;
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueParseFailureThrottle value)
        {
            _ = value;
        }
    }
}
