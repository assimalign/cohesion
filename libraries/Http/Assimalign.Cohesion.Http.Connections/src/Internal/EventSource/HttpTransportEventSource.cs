using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>Transport decisions that are not owned by a host's request telemetry.</summary>
[EventSource(Name = "Assimalign.Cohesion.Http.Connections")]
internal sealed class HttpTransportEventSource : EventSource
{
    public static readonly HttpTransportEventSource Log = new();

    private readonly Meter _meter = new("Assimalign.Cohesion.Http.Connections");
    private readonly Counter<long> _rejectedRequestsMetric;
    private readonly Counter<long> _protocolErrorsMetric;
    private readonly Counter<long> _responseRateAbortsMetric;
    private readonly Counter<long> _responseAbortsMetric;
    private readonly HttpTransportEventThrottle _rejectionThrottle = new();
    private readonly HttpTransportEventThrottle _connectionThrottle = new();
    private readonly HttpTransportEventThrottle _streamThrottle = new();
    private readonly HttpTransportEventThrottle _rateThrottle = new();
    private readonly HttpTransportEventThrottle _responseThrottle = new();
    private long _rejectedRequests;
    private long _connectionErrors;
    private long _streamResets;
    private long _responseRateAborts;
    private long _responseAborts;
    private IncrementingPollingCounter? _rejectedRequestsCounter;
    private IncrementingPollingCounter? _connectionErrorsCounter;
    private IncrementingPollingCounter? _streamResetsCounter;
    private IncrementingPollingCounter? _responseRateAbortsCounter;
    private IncrementingPollingCounter? _responseAbortsCounter;

    private HttpTransportEventSource()
    {
        _rejectedRequestsMetric = _meter.CreateCounter<long>("cohesion.http.server.rejected_requests", "{request}", "Requests rejected by the HTTP transport.");
        _protocolErrorsMetric = _meter.CreateCounter<long>("cohesion.http.server.protocol_errors", "{error}", "HTTP connection errors and stream resets decided by the server.");
        _responseRateAbortsMetric = _meter.CreateCounter<long>("cohesion.http.server.response_rate_aborts", "{abort}", "Minimum response-rate enforcement aborts, including stalled connection output.");
        _responseAbortsMetric = _meter.CreateCounter<long>("cohesion.http.server.response_aborts", "{response}", "Responses ended on the client's account.");
    }

    internal long RejectedRequests => Interlocked.Read(ref _rejectedRequests);
    internal long ConnectionErrors => Interlocked.Read(ref _connectionErrors);
    internal long StreamResets => Interlocked.Read(ref _streamResets);
    internal long ResponseRateAborts => Interlocked.Read(ref _responseRateAborts);
    internal long ResponseAborts => Interlocked.Read(ref _responseAborts);

    [NonEvent]
    public void RequestRejected(ConnectionId connectionId, string protocol, long streamId, int statusCode)
    {
        Interlocked.Increment(ref _rejectedRequests);
        if (_rejectedRequestsMetric.Enabled)
        {
            _rejectedRequestsMetric.Add(1,
                new KeyValuePair<string, object?>("network.protocol.version", protocol),
                new KeyValuePair<string, object?>("cohesion.http.rejection.status_code", statusCode));
        }
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _rejectionThrottle.TryAcquire(out int suppressedCount))
        {
            RequestRejected(connectionId.ToString(), protocol, streamId, statusCode, suppressedCount);
        }
    }

    [NonEvent]
    public void ConnectionError(ConnectionId connectionId, string protocol, long errorCode)
    {
        Interlocked.Increment(ref _connectionErrors);
        RecordProtocolError(protocol, errorCode, "connection");
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _connectionThrottle.TryAcquire(out int suppressedCount))
        {
            ConnectionError(connectionId.ToString(), protocol, errorCode, suppressedCount);
        }
    }

    [NonEvent]
    public void StreamReset(ConnectionId connectionId, string protocol, long streamId, long errorCode)
    {
        Interlocked.Increment(ref _streamResets);
        RecordProtocolError(protocol, errorCode, "stream");
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _streamThrottle.TryAcquire(out int suppressedCount))
        {
            StreamReset(connectionId.ToString(), protocol, streamId, errorCode, suppressedCount);
        }
    }

    [NonEvent]
    public void ResponseRateAborted(ConnectionId connectionId, string protocol, long streamId)
    {
        Interlocked.Increment(ref _responseRateAborts);
        if (_responseRateAbortsMetric.Enabled)
        {
            _responseRateAbortsMetric.Add(1, new KeyValuePair<string, object?>("network.protocol.version", protocol));
        }
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _rateThrottle.TryAcquire(out int suppressedCount))
        {
            ResponseRateAborted(connectionId.ToString(), protocol, streamId, suppressedCount);
        }
    }

    [NonEvent]
    public void ResponseAbortedByClient(ConnectionId connectionId, string protocol, long streamId)
    {
        Interlocked.Increment(ref _responseAborts);
        if (_responseAbortsMetric.Enabled)
        {
            _responseAbortsMetric.Add(1, new KeyValuePair<string, object?>("network.protocol.version", protocol));
        }
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _responseThrottle.TryAcquire(out int suppressedCount))
        {
            ResponseAbortedByClient(connectionId.ToString(), protocol, streamId, suppressedCount);
        }
    }

    [NonEvent]
    private void RecordProtocolError(string protocol, long errorCode, string scope)
    {
        if (_protocolErrorsMetric.Enabled)
        {
            _protocolErrorsMetric.Add(1,
                new KeyValuePair<string, object?>("network.protocol.version", protocol),
                new KeyValuePair<string, object?>("error.type", errorCode.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, object?>("cohesion.http.error.scope", scope));
        }
    }

    [Event(1, Level = EventLevel.Warning, Message = "Connection {0}, HTTP/{1} stream {2}: transport rejected request with {3}; {4} suppressed")]
    private void RequestRejected(string connectionId, string protocol, long streamId, int statusCode, int suppressedCount)
        => WriteEvent(1, connectionId, protocol, streamId, statusCode, suppressedCount);

    [Event(2, Level = EventLevel.Warning, Message = "Connection {0}, HTTP/{1}: connection error {2}; {3} suppressed")]
    private void ConnectionError(string connectionId, string protocol, long errorCode, int suppressedCount)
        => WriteEvent(2, connectionId, protocol, errorCode, suppressedCount);

    [Event(3, Level = EventLevel.Warning, Message = "Connection {0}, HTTP/{1} stream {2}: reset with {3}; {4} suppressed")]
    private void StreamReset(string connectionId, string protocol, long streamId, long errorCode, int suppressedCount)
        => WriteEvent(3, connectionId, protocol, streamId, errorCode, suppressedCount);

    [Event(4, Level = EventLevel.Warning, Message = "Connection {0}, HTTP/{1} stream {2}: response rate abort; {3} suppressed")]
    private void ResponseRateAborted(string connectionId, string protocol, long streamId, int suppressedCount)
        => WriteEvent(4, connectionId, protocol, streamId, suppressedCount);

    [Event(5, Level = EventLevel.Warning, Message = "Connection {0}, HTTP/{1} stream {2}: response ended on the client's account; {3} suppressed")]
    private void ResponseAbortedByClient(string connectionId, string protocol, long streamId, int suppressedCount)
        => WriteEvent(5, connectionId, protocol, streamId, suppressedCount);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }
        _rejectedRequestsCounter ??= CreateCounter("rejected-requests", "Transport rejected requests", () => RejectedRequests);
        _connectionErrorsCounter ??= CreateCounter("connection-errors", "HTTP connection errors", () => ConnectionErrors);
        _streamResetsCounter ??= CreateCounter("stream-resets", "HTTP stream resets", () => StreamResets);
        _responseRateAbortsCounter ??= CreateCounter("response-rate-aborts", "Slow reader response aborts", () => ResponseRateAborts);
        _responseAbortsCounter ??= CreateCounter("response-aborts", "Responses ended on the client's account", () => ResponseAborts);
    }

    [NonEvent]
    private IncrementingPollingCounter CreateCounter(string name, string displayName, Func<double> value)
        => new(name, this, value) { DisplayName = displayName, DisplayRateTimeScale = TimeSpan.FromSeconds(1) };
}
