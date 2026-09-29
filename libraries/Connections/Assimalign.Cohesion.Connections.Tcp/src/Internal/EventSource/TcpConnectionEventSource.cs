using System;
using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// The TCP driver's diagnostics: listener and connection lifecycle events, and connection counters.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools enable
/// it by its assembly name, <c>Assimalign.Cohesion.Connections.Tcp</c>; applications forward it into their
/// logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every connection reports
/// <c>ConnectionOpened</c> exactly once and <c>ConnectionClosed</c> exactly once, which keeps the
/// <c>current-connections</c> gauge exact.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Connections.Tcp")]
internal sealed class TcpConnectionEventSource : EventSource
{
    public static readonly TcpConnectionEventSource Log = new();

    private PollingCounter? _currentConnectionsCounter;
    private PollingCounter? _totalConnectionsCounter;
    private IncrementingPollingCounter? _connectionRateCounter;
    private long _currentConnections;
    private long _totalConnections;

    private TcpConnectionEventSource()
    {
    }

    /// <summary>The connections opened and not yet closed.</summary>
    internal long CurrentConnections => Volatile.Read(ref _currentConnections);

    /// <summary>The connections opened since the process started.</summary>
    internal long TotalConnections => Volatile.Read(ref _totalConnections);

    [NonEvent]
    public void ListenerBound(ListenerId listenerId, ConnectionProtocol protocol, EndPoint endPoint)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ListenerBound(listenerId.ToString(), protocol.ToString(), FormatEndPoint(endPoint));
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
    public void ConnectionOpened(
        ConnectionId connectionId,
        ListenerId listenerId,
        ConnectionProtocol protocol,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
    {
        Interlocked.Increment(ref _currentConnections);
        Interlocked.Increment(ref _totalConnections);

        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ConnectionOpened(
                connectionId.ToString(),
                FormatListenerId(listenerId),
                protocol.ToString(),
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
    public void ConnectionFinished(ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ConnectionFinished(connectionId.ToString());
        }
    }

    [NonEvent]
    public void ConnectionPaused(ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ConnectionPaused(connectionId.ToString());
        }
    }

    [NonEvent]
    public void ConnectionResumed(ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ConnectionResumed(connectionId.ToString());
        }
    }

    [NonEvent]
    public void ConnectionReset(ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ConnectionReset(connectionId.ToString());
        }
    }

    [NonEvent]
    public void ConnectionError(ConnectionId connectionId, string operation, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ConnectionError(connectionId.ToString(), operation, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Listener {0} bound {1} endpoint {2}")]
    private void ListenerBound(string listenerId, string protocol, string endPoint)
        => WriteEvent(1, listenerId, protocol, endPoint);

    [Event(2, Level = EventLevel.Informational, Message = "Listener {0} closed")]
    private void ListenerClosed(string listenerId)
        => WriteEvent(2, listenerId);

    [Event(3, Level = EventLevel.Informational, Message = "Connection {0} opened over {2}: local {3}, remote {4}, listener {1}")]
    private void ConnectionOpened(string connectionId, string listenerId, string protocol, string localEndPoint, string remoteEndPoint)
        => WriteEvent(3, connectionId, listenerId, protocol, localEndPoint, remoteEndPoint);

    [Event(4, Level = EventLevel.Informational, Message = "Connection {0} closed")]
    private void ConnectionClosed(string connectionId)
        => WriteEvent(4, connectionId);

    [Event(5, Level = EventLevel.Verbose, Message = "Connection {0} received the end of its peer's stream")]
    private void ConnectionFinished(string connectionId)
        => WriteEvent(5, connectionId);

    [Event(6, Level = EventLevel.Verbose, Message = "Connection {0} paused receiving under application back-pressure")]
    private void ConnectionPaused(string connectionId)
        => WriteEvent(6, connectionId);

    [Event(7, Level = EventLevel.Verbose, Message = "Connection {0} resumed receiving")]
    private void ConnectionResumed(string connectionId)
        => WriteEvent(7, connectionId);

    [Event(8, Level = EventLevel.Verbose, Message = "Connection {0} was reset by its peer")]
    private void ConnectionReset(string connectionId)
        => WriteEvent(8, connectionId);

    [Event(9, Level = EventLevel.Error, Message = "Connection {0} failed while {1}: {2}: {3}")]
    private void ConnectionError(string connectionId, string operation, string exceptionType, string exceptionMessage)
        => WriteEvent(9, connectionId, operation, exceptionType, exceptionMessage);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // Created on the first enable command, as the runtime's own sources do, and kept for the source's
        // lifetime. The backing fields are maintained whether or not anyone listens, so a tool that attaches
        // late still reads exact values.
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
    }

    private static string FormatListenerId(ListenerId listenerId)
        => listenerId == ListenerId.Empty ? string.Empty : listenerId.ToString();

    private static string FormatEndPoint(EndPoint? endPoint)
        => endPoint?.ToString() ?? string.Empty;
}
