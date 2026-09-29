using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

/// <summary>
/// Records the events one event source writes, and optionally the names of the counters it publishes.
/// </summary>
internal sealed class EventSourceRecorder : EventListener
{
    private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
    private readonly ConcurrentDictionary<string, bool> _counterNames = new(StringComparer.Ordinal);
    private readonly EventSource _eventSource;

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
            }

            return;
        }

        _events.Enqueue(eventData);
    }
}
