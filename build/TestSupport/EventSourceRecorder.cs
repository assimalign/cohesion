using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Deviates from the repo RootNamespace rule per shared-source design: linked test assemblies
// use a neutral test-support namespace. This EventListener is test-only; its instances never cross
// an assembly boundary, and no shipped project compiles this shared folder.
namespace Assimalign.Cohesion.Testing;

/// <summary>
/// Records the events one event source writes, and optionally the names of the counters it publishes.
/// </summary>
internal sealed class EventSourceRecorder : EventListener
{
    private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
    private readonly ConcurrentDictionary<string, bool> _counterNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CounterSample> _counterSamples = new(StringComparer.Ordinal);
    private readonly EventSource _eventSource;
    private long _counterSequence;

    public EventSourceRecorder(EventSource eventSource, EventLevel level, TimeSpan? counterInterval = null)
    {
        _eventSource = eventSource;

        Dictionary<string, string?>? arguments = counterInterval is { } interval
            ? new() { ["EventCounterIntervalSec"] = interval.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null;

        EnableEvents(eventSource, level, EventKeywords.All, arguments);
    }

    /// <summary>The events written so far, excluding counter payloads.</summary>
    public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

    /// <summary>The sequence of the latest counter payload, for waiting on a fresh sample.</summary>
    public long CounterSequence => Interlocked.Read(ref _counterSequence);

    /// <summary>Waits for a counter value published after the supplied sequence.</summary>
    public async Task<CounterSample> WaitForCounterAsync(string counterName, long afterSequence, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_counterSamples.TryGetValue(counterName, out CounterSample sample) && sample.Sequence > afterSequence)
            {
                return sample;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    /// <summary>Waits until every named counter has published at least one payload.</summary>
    public async Task WaitForCountersAsync(IReadOnlyCollection<string> counterNames, CancellationToken cancellationToken)
    {
        while (!counterNames.All(_counterNames.ContainsKey))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!ReferenceEquals(eventData.EventSource, _eventSource))
        {
            return;
        }

        if (string.Equals(eventData.EventName, "EventCounters", StringComparison.Ordinal))
        {
            if (eventData.Payload is [IDictionary<string, object?> counter, ..] &&
                counter.TryGetValue("Name", out object? name) &&
                name is string counterName)
            {
                _counterNames[counterName] = true;

                if ((counter.TryGetValue("Mean", out object? value) || counter.TryGetValue("Increment", out value)) &&
                    value is double number)
                {
                    _counterSamples[counterName] = new(Interlocked.Increment(ref _counterSequence), number);
                }
            }

            return;
        }

        _events.Enqueue(eventData);
    }

    /// <summary>A polling counter's mean, or an incrementing counter's increment, and its publication sequence.</summary>
    public readonly record struct CounterSample(long Sequence, double Value);
}
