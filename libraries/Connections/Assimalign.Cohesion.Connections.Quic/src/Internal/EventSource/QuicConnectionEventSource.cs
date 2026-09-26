using System;
using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Quic.Internal;

/// <summary>
/// The QUIC driver's diagnostics: listener, connection, and stream lifecycle events, and connection and
/// stream counters.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools enable
/// it by its assembly name, <c>Assimalign.Cohesion.Connections.Quic</c>; applications forward it into their
/// logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Streams report at Verbose because a
/// multiplexed protocol such as HTTP/3 opens one per request. Every connection and every stream reports its
/// open and close exactly once, which keeps the <c>current-*</c> gauges exact.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Connections.Quic")]
internal sealed class QuicConnectionEventSource : EventSource
{
    public static readonly QuicConnectionEventSource Log = new();

    private PollingCounter? _currentConnectionsCounter;
    private PollingCounter? _totalConnectionsCounter;
    private IncrementingPollingCounter? _connectionRateCounter;
    private PollingCounter? _currentStreamsCounter;
    private IncrementingPollingCounter? _streamRateCounter;
    private long _currentConnections;
    private long _totalConnections;
    private long _currentStreams;
    private long _totalStreams;

    private QuicConnectionEventSource()
    {
    }

    /// <summary>The QUIC connections opened and not yet closed.</summary>
    internal long CurrentConnections => Volatile.Read(ref _currentConnections);

    /// <summary>The QUIC connections opened since the process started.</summary>
    internal long TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>The QUIC streams opened and not yet closed.</summary>
    internal long CurrentStreams => Volatile.Read(ref _currentStreams);

    [NonEvent]
    public void ListenerBound(ListenerId listenerId, EndPoint endPoint)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ListenerBound(listenerId.ToString(), FormatEndPoint(endPoint));
        }
    }

    [NonEvent]
    public void ListenerClosed(ListenerId listenerId)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ListenerClosed(listenerId.ToString());
        }
    }

    [NonEvent]
    public void ConnectionOpened(ConnectionId connectionId, ListenerId listenerId, EndPoint? localEndPoint, EndPoint? remoteEndPoint)
    {
        Interlocked.Increment(ref _currentConnections);
        Interlocked.Increment(ref _totalConnections);

        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ConnectionOpened(
                connectionId.ToString(),
                listenerId == ListenerId.Empty ? string.Empty : listenerId.ToString(),
                FormatEndPoint(localEndPoint),
                FormatEndPoint(remoteEndPoint));
        }
    }

    [NonEvent]
    public void ConnectionClosed(ConnectionId connectionId)
    {
        Interlocked.Decrement(ref _currentConnections);

        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ConnectionClosed(connectionId.ToString());
        }
    }

    [NonEvent]
    public void StreamOpened(ConnectionId streamId, ConnectionId connectionId, ConnectionDirection direction)
    {
        Interlocked.Increment(ref _currentStreams);
        Interlocked.Increment(ref _totalStreams);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            StreamOpened(streamId.ToString(), connectionId.ToString(), direction.ToString());
        }
    }

    [NonEvent]
    public void StreamClosed(ConnectionId streamId)
    {
        Interlocked.Decrement(ref _currentStreams);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            StreamClosed(streamId.ToString());
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Listener {0} bound endpoint {1}")]
    private void ListenerBound(string listenerId, string endPoint)
        => WriteEvent(1, listenerId, endPoint);

    [Event(2, Level = EventLevel.Informational, Message = "Listener {0} closed")]
    private void ListenerClosed(string listenerId)
        => WriteEvent(2, listenerId);

    [Event(3, Level = EventLevel.Informational, Message = "Connection {0} opened: local {2}, remote {3}, listener {1}")]
    private void ConnectionOpened(string connectionId, string listenerId, string localEndPoint, string remoteEndPoint)
        => WriteEvent(3, connectionId, listenerId, localEndPoint, remoteEndPoint);

    [Event(4, Level = EventLevel.Informational, Message = "Connection {0} closed")]
    private void ConnectionClosed(string connectionId)
        => WriteEvent(4, connectionId);

    [Event(5, Level = EventLevel.Verbose, Message = "Stream {0} opened on connection {1} ({2})")]
    private void StreamOpened(string streamId, string connectionId, string direction)
        => WriteEvent(5, streamId, connectionId, direction);

    [Event(6, Level = EventLevel.Verbose, Message = "Stream {0} closed")]
    private void StreamClosed(string streamId)
        => WriteEvent(6, streamId);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // Created on the first enable command and kept for the source's lifetime; the backing fields are
        // maintained whether or not anyone listens, so a tool that attaches late still reads exact values.
        _currentConnectionsCounter ??= new PollingCounter("current-connections", this, () => Volatile.Read(ref _currentConnections))
        {
            DisplayName = "Current Connections",
        };
        _totalConnectionsCounter ??= new PollingCounter("total-connections", this, () => Volatile.Read(ref _totalConnections))
        {
            DisplayName = "Total Connections",
        };
        _connectionRateCounter ??= new IncrementingPollingCounter("connections-per-second", this, () => Volatile.Read(ref _totalConnections))
        {
            DisplayName = "Connection Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _currentStreamsCounter ??= new PollingCounter("current-streams", this, () => Volatile.Read(ref _currentStreams))
        {
            DisplayName = "Current Streams",
        };
        _streamRateCounter ??= new IncrementingPollingCounter("streams-per-second", this, () => Volatile.Read(ref _totalStreams))
        {
            DisplayName = "Stream Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
    }

    private static string FormatEndPoint(EndPoint? endPoint)
        => endPoint?.ToString() ?? string.Empty;
}
