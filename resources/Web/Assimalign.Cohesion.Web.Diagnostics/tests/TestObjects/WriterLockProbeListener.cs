using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

internal sealed class WriterLockProbeListener : EventListener
{
    private readonly EventSource _source;
    private readonly int _eventId;
    private readonly Action _probe;
    private int _started;

    public WriterLockProbeListener(EventSource source, int eventId, Action probe)
    {
        _source = source;
        _eventId = eventId;
        _probe = probe;
        EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
    }

    public bool CompletedInsideCallback { get; private set; }

    public Task? ProbeTask { get; private set; }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!ReferenceEquals(eventData.EventSource, _source) || eventData.EventId != _eventId ||
            Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        ProbeTask = Task.Run(_probe);
        CompletedInsideCallback = ProbeTask.Wait(TimeSpan.FromSeconds(2));
    }
}
