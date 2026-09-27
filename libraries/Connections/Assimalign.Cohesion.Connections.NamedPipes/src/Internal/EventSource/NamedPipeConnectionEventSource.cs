using System;
using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Connections.NamedPipes.Internal;

/// <summary>
/// The named-pipe driver's diagnostics: listener and connection lifecycle events, and connection counters.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools enable
/// it by its assembly name, <c>Assimalign.Cohesion.Connections.NamedPipes</c>; applications forward it into
/// their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Accepted and dialed connections alike
/// report their open and close exactly once, which keeps the <c>current-connections</c> gauge exact.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Connections.NamedPipes")]
internal sealed class NamedPipeConnectionEventSource : EventSource
{
    public static readonly NamedPipeConnectionEventSource Log = new();

    private PollingCounter? _currentConnectionsCounter;
    private PollingCounter? _totalConnectionsCounter;
    private IncrementingPollingCounter? _connectionRateCounter;
    private long _currentConnections;
    private long _totalConnections;

    private NamedPipeConnectionEventSource()
    {
    }

    /// <summary>The connections opened and not yet closed.</summary>
    internal long CurrentConnections => Volatile.Read(ref _currentConnections);

    /// <summary>The connections opened since the process started.</summary>
    internal long TotalConnections => Volatile.Read(ref _totalConnections);

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

    [Event(1, Level = EventLevel.Informational, Message = "Listener {0} bound pipe {1}")]
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
    }

    private static string FormatEndPoint(EndPoint? endPoint)
        => endPoint?.ToString() ?? string.Empty;
}
