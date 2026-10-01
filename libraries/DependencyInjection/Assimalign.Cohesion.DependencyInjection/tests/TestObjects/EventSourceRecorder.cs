using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Records the events one event source writes.
/// </summary>
internal sealed class EventSourceRecorder : EventListener
{
    private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
    private readonly EventSource _eventSource;

    public EventSourceRecorder(EventSource eventSource, EventLevel level)
    {
        _eventSource = eventSource;
        EnableEvents(eventSource, level, EventKeywords.All);
    }

    /// <summary>The events written so far.</summary>
    public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (ReferenceEquals(eventData.EventSource, _eventSource))
        {
            _events.Enqueue(eventData);
        }
    }
}
