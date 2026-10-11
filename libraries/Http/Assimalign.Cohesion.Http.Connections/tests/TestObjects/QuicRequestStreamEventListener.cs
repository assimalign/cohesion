using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>Observes the QUIC driver's public events and gauge without reaching into its internals.</summary>
internal sealed class QuicRequestStreamEventListener : EventListener
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, bool> _streams = new();
    private readonly HashSet<string> _closedStreams = new();
    private readonly HashSet<string> _connections = new();
    private TaskCompletionSource<double> _nextGauge = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestStreamsOpened;
    private int _duplicateCloses;

    public int ConnectionCount { get { lock (_gate) { return _connections.Count; } } }

    public int RequestStreamsOpened { get { lock (_gate) { return _requestStreamsOpened; } } }

    public int CurrentRequestStreams { get { lock (_gate) { return _streams.Values.Count(isRequest => isRequest); } } }

    public int CurrentCriticalStreams { get { lock (_gate) { return _streams.Values.Count(isRequest => !isRequest); } } }

    public int DuplicateCloses { get { lock (_gate) { return _duplicateCloses; } } }

    public async Task<double> ReadNextGaugeAsync(CancellationToken cancellationToken)
    {
        Task<double> sample;
        lock (_gate)
        {
            sample = _nextGauge.Task;
        }

        return await sample.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Assimalign.Cohesion.Connections.Quic")
        {
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All,
                new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "0.05" });
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.Payload is null)
        {
            return;
        }

        lock (_gate)
        {
            if (eventData.EventId == 3)
            {
                _connections.Add((string)eventData.Payload[0]!);
            }
            else if (eventData.EventId == 5)
            {
                bool isRequest = (string)eventData.Payload[2]! == "Bidirectional";
                _streams.Add((string)eventData.Payload[0]!, isRequest);
                if (isRequest)
                {
                    _requestStreamsOpened++;
                }
            }
            else if (eventData.EventId == 6)
            {
                string streamId = (string)eventData.Payload[0]!;
                if (!_closedStreams.Add(streamId))
                {
                    _duplicateCloses++;
                }

                _streams.Remove(streamId);
            }
            else if (eventData.EventName == "EventCounters"
                && eventData.Payload[0] is IDictionary<string, object> counter
                && counter["Name"] is "current-streams")
            {
                TaskCompletionSource<double> next = _nextGauge;
                _nextGauge = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                next.TrySetResult(Convert.ToDouble(counter["Mean"]));
            }
        }
    }
}
