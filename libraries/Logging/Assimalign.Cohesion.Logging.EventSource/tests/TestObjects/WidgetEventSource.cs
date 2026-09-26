using System;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// An event source with one event per level, created under a unique name per test so that no other test
/// and no other listener in the process shares it.
/// </summary>
internal sealed class WidgetEventSource : EventSource
{
    private PollingCounter? _widgetsCounter;
    private long _widgets;

    private WidgetEventSource(string name)
        : base(name, EventSourceSettings.EtwManifestEventFormat)
    {
    }

    /// <summary>Creates a source named <paramref name="prefix"/> followed by a unique suffix.</summary>
    public static WidgetEventSource Create(string prefix = EventSourceForwardingOptions.CohesionSourcePrefix)
        => new(prefix + "Logging.EventSource.Tests.Widget" + Guid.NewGuid().ToString("N"));

    [Event(1, Level = EventLevel.Informational, Message = "Widget {0} started with {1} parts")]
    public void WidgetStarted(string widgetId, int partCount)
    {
        Interlocked.Increment(ref _widgets);
        WriteEvent(1, widgetId, partCount);
    }

    [Event(2, Level = EventLevel.Verbose, Message = "Widget {0} ticked")]
    public void WidgetTicked(string widgetId) => WriteEvent(2, widgetId);

    [Event(3, Level = EventLevel.Warning)]
    public void WidgetDegraded(string widgetId) => WriteEvent(3, widgetId);

    [Event(4, Level = EventLevel.Error, Message = "Widget {0} failed: {1}")]
    public void WidgetFailed(string widgetId, string reason) => WriteEvent(4, widgetId, reason);

    [Event(5, Level = EventLevel.Critical, Message = "Widget {0} lost")]
    public void WidgetLost(string widgetId) => WriteEvent(5, widgetId);

    // The template names a third argument the event does not carry.
    [Event(6, Level = EventLevel.Informational, Message = "Widget {0} measured {1} of {2}")]
    public void WidgetMeasured(string widgetId, int value) => WriteEvent(6, widgetId, value);

    // Deliberately wrong: two parameters, one written. EventSource reports the mismatch as event 0.
    [Event(7, Level = EventLevel.Informational)]
    public void WidgetMiswritten(string widgetId, string reason) => WriteEvent(7, widgetId);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _widgetsCounter ??= new PollingCounter("widgets", this, () => Volatile.Read(ref _widgets));
        }
    }
}
