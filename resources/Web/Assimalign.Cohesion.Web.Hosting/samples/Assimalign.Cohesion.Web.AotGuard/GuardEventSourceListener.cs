using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;

namespace Assimalign.Cohesion.Web.AotGuard;

/// <summary>
/// Verifies that the server's manifest-based events and their payload names survive NativeAOT.
/// </summary>
internal sealed class GuardEventSourceListener : EventListener
{
    private const string sourceName = "Assimalign.Cohesion.Web.Server";
    private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

    /// <summary>
    /// Checks the lifecycle after the server stops, returning the smoke check's verdict.
    /// </summary>
    /// <returns>Whether both lifecycle events arrived once with valid, matching payloads and no event 0.</returns>
    public bool Verify()
    {
        EventWrittenEventArgs[] events = _events.ToArray();
        EventWrittenEventArgs[] bound = events.Where(entry => entry.EventId == 1).ToArray();
        EventWrittenEventArgs[] stopped = events.Where(entry => entry.EventId == 9).ToArray();
        EventWrittenEventArgs[] errors = events.Where(entry => entry.EventId == 0).ToArray();
        bool valid = errors.Length == 0 && bound.Length == 1 && stopped.Length == 1;

        if (valid)
        {
            valid = bound[0].EventName == "ServerBound"
                && bound[0].PayloadNames is { } boundNames
                && boundNames.SequenceEqual(["serverId", "protocols", "endPoints", "maxConcurrentConnections"])
                && bound[0].Payload is { Count: 4 } boundPayload
                && boundPayload[0] is long serverId && serverId > 0
                && boundPayload[1] is string protocols && protocols.Length > 0
                && boundPayload[2] is string
                && boundPayload[3] is int
                && stopped[0].EventName == "ServerStopped"
                && stopped[0].PayloadNames is { } stoppedNames
                && stoppedNames.SequenceEqual(["serverId", "elapsedMilliseconds", "aborted"])
                && stopped[0].Payload is { Count: 3 } stoppedPayload
                && Equals(stoppedPayload[0], serverId)
                && stoppedPayload[1] is double elapsed && elapsed >= 0
                && stoppedPayload[2] is false;
        }

        Console.WriteLine(valid
            ? "PASS: server EventSource lifecycle has its declared payload names and no event 0."
            : $"FAIL: server EventSource lifecycle (ServerBound={bound.Length}, ServerStopped={stopped.Length}, event 0={errors.Length}).");
        return valid;
    }

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == sourceName)
        {
            EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
        }
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == sourceName)
        {
            _events.Enqueue(eventData);
        }
    }
}
