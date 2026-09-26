using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// Plays the part of <c>dotnet-counters</c>: enables one source's counters at a short interval and signals
/// when the first <c>EventCounters</c> payload arrives.
/// </summary>
internal sealed class CounterPayloadListener : EventListener
{
    private readonly TaskCompletionSource _firstPayload = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _sourceName;

    /// <summary>Completes when the first counter payload for the enabled source arrives.</summary>
    public Task FirstPayload => _firstPayload.Task;

    /// <summary>Enables <paramref name="eventSource"/> with a 100 ms counter interval.</summary>
    public void EnableCounters(EventSource eventSource)
    {
        Volatile.Write(ref _sourceName, eventSource.Name);
        EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All, new Dictionary<string, string?>
        {
            ["EventCounterIntervalSec"] = "0.1",
        });
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (string.Equals(eventData.EventName, "EventCounters", StringComparison.Ordinal) &&
            string.Equals(eventData.EventSource.Name, Volatile.Read(ref _sourceName), StringComparison.Ordinal))
        {
            _firstPayload.TrySetResult();
        }
    }
}
