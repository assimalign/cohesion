using System;
using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Udp.Internal;

/// <summary>
/// The UDP driver's diagnostics: datagram connection lifecycle events and connection counters.
/// </summary>
/// <remarks>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools enable
/// it by its assembly name, <c>Assimalign.Cohesion.Connections.Udp</c>; applications forward it into their
/// logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. A datagram connection is a bound (server) or
/// connected (client) socket; each reports its open and close exactly once. Individual datagrams are not
/// events: per-message tracing on a message-oriented transport would cost more than it tells.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Connections.Udp")]
internal sealed class UdpConnectionEventSource : EventSource
{
    public static readonly UdpConnectionEventSource Log = new();

    private PollingCounter? _currentConnectionsCounter;
    private PollingCounter? _totalConnectionsCounter;
    private long _currentConnections;
    private long _totalConnections;

    private UdpConnectionEventSource()
    {
    }

    /// <summary>The datagram connections opened and not yet closed.</summary>
    internal long CurrentConnections => Volatile.Read(ref _currentConnections);

    /// <summary>The datagram connections opened since the process started.</summary>
    internal long TotalConnections => Volatile.Read(ref _totalConnections);

    [NonEvent]
    public void ConnectionOpened(ConnectionId connectionId, EndPoint localEndPoint, EndPoint? remoteEndPoint)
    {
        Interlocked.Increment(ref _currentConnections);
        Interlocked.Increment(ref _totalConnections);

        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ConnectionOpened(
                connectionId.ToString(),
                remoteEndPoint is null ? "bind" : "connect",
                localEndPoint.ToString() ?? string.Empty,
                remoteEndPoint?.ToString() ?? string.Empty);
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

    [Event(1, Level = EventLevel.Informational, Message = "Datagram connection {0} opened by {1}: local {2}, remote {3}")]
    private void ConnectionOpened(string connectionId, string mode, string localEndPoint, string remoteEndPoint)
        => WriteEvent(1, connectionId, mode, localEndPoint, remoteEndPoint);

    [Event(2, Level = EventLevel.Informational, Message = "Datagram connection {0} closed")]
    private void ConnectionClosed(string connectionId)
        => WriteEvent(2, connectionId);

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
            DisplayName = "Current Datagram Connections",
        };
        _totalConnectionsCounter ??= new PollingCounter("total-connections", this, () => Volatile.Read(ref _totalConnections))
        {
            DisplayName = "Total Datagram Connections",
        };
    }
}
