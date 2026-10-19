using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Internal;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Records the events the storage event source writes, and optionally the names of the counters
/// it publishes. Every event's first payload is its <c>database</c>, so a test reads only the
/// events of the storage it named (<see cref="For"/>).
/// </summary>
internal sealed class StorageEventRecorder : EventListener
{
    private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
    private readonly ConcurrentDictionary<string, bool> _counterNames = new(StringComparer.Ordinal);

    public StorageEventRecorder(EventLevel level, EventKeywords keywords = EventKeywords.All, TimeSpan? counterInterval = null)
    {
        Dictionary<string, string?>? arguments = counterInterval is { } interval
            ? new() { ["EventCounterIntervalSec"] = interval.TotalSeconds.ToString(CultureInfo.InvariantCulture) }
            : null;

        EnableEvents(StorageEventSource.Log, level, keywords, arguments);
    }

    /// <summary>Gets every event written so far, counter payloads excluded.</summary>
    public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

    /// <summary>Gets the events written so far for one database, in order.</summary>
    /// <param name="database">The storage's name.</param>
    public IReadOnlyList<EventWrittenEventArgs> For(string database)
        => _events.Where(e => e.Payload is { Count: > 0 } payload && Equals(payload[0], database)).ToArray();

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
        if (!ReferenceEquals(eventData.EventSource, StorageEventSource.Log))
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
