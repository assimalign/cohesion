using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

internal sealed class HealthEventRecorder : EventListener
{
    private int _healthTransitions;

    internal int HealthTransitions => Volatile.Read(ref _healthTransitions);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Assimalign.Cohesion.Database.Hosting")
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId is 16 or 17) { Interlocked.Increment(ref _healthTransitions); }
    }
}
