using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Tests;

// This filtered listener discovers the cross-assembly provider as storage initializes it.
// Event 8 comes only from FlushPendingCommits; inline self-help reports event 9 instead.
internal sealed class GroupCommitRecorder : EventListener
{
    private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

    internal int FlushCount(string database) => Count(database, 8);
    internal int MissedCount(string database) => Count(database, 9);

    private int Count(string database, int id) => _events.Count(written => written.EventId == id
        && written.Payload is { Count: > 0 } payload
        && (Equals(payload[0], database) || Equals(payload[0], database + "/catalog")));

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Assimalign.Cohesion.Database.Storage")
        {
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId is 8 or 9) { _events?.Enqueue(eventData); }
    }
}
