using System;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class TimeoutLockListener : EventListener
{
    internal Action<EventWrittenEventArgs>? Stopped { get; set; }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Assimalign.Cohesion.Database.Transactions")
        {
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName == "LockWaitStop") { Stopped?.Invoke(eventData); }
    }
}
