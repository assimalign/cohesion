using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Recorder = Assimalign.Cohesion.Testing.EventSourceRecorder;

namespace Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

/// <summary>Wraps the canonical event recorder with a history of every rate increment.</summary>
internal sealed class TransactionRateRecorder : EventListener
{
    private static readonly string[] _rates = ["transactions-per-second", "commits-per-second", "rollbacks-per-second"];
    private readonly object _sync = new();
    private readonly List<RateSample> _samples = new();
    private readonly EventSource _source;
    private readonly Recorder _recorder;
    private long _sequence;

    internal TransactionRateRecorder(EventSource source)
    {
        _source = source;
        _recorder = new Recorder(source, EventLevel.Verbose);
        EnableEvents(source, EventLevel.Verbose, EventKeywords.All, new Dictionary<string, string?>
        {
            ["EventCounterIntervalSec"] = TimeSpan.FromMilliseconds(100).TotalSeconds.ToString(CultureInfo.InvariantCulture),
        });
    }

    internal IReadOnlyList<EventWrittenEventArgs> Events => _recorder.Events;

    /// <summary>
    /// Waits for all rates twice after the call. A callback can read a total before the call
    /// and publish it afterwards; the second publication includes every preceding operation.
    /// The returned sequence bounds a sum without dropping any intervening samples.
    /// </summary>
    internal async Task<long> WaitForBoundaryAsync(CancellationToken cancellationToken)
    {
        long after;
        lock (_sync) { after = _sequence; }
        for (int publication = 0; publication < 2; publication++)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_rates.All(rate => _samples.Any(sample => sample.Name == rate && sample.Sequence > after)))
                    {
                        after = _sequence;
                        break;
                    }
                }
                await Task.Delay(10, cancellationToken);
            }
        }
        return after;
    }

    internal double Sum(string rate, long after, long through)
    {
        lock (_sync)
        {
            return _samples.Where(sample => sample.Name == rate && sample.Sequence > after && sample.Sequence <= through)
                .Sum(sample => sample.Increment);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _recorder.Dispose();
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (ReferenceEquals(eventData.EventSource, _source) && eventData.EventName == "EventCounters"
            && eventData.Payload is [IDictionary<string, object?> counter, ..]
            && counter.TryGetValue("Name", out object? name) && name is string rate && _rates.Contains(rate)
            && counter.TryGetValue("Increment", out object? value) && value is double increment)
        {
            lock (_sync) { _samples.Add(new RateSample(++_sequence, rate, increment)); }
        }
    }

    private readonly record struct RateSample(long Sequence, string Name, double Increment);
}
