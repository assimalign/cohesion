using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Internal;

[EventSource(Name = "Assimalign.Cohesion.Connections")]
internal sealed class ConnectionEventSource : EventSource
{
    public static readonly ConnectionEventSource Log = new ConnectionEventSource();

    private PollingCounter? _connectionsStartedCounter;
    private PollingCounter? _connectionsStoppedCounter;
    private PollingCounter? _connectionsTimedOutCounter;
    private PollingCounter? _currentConnectionsCounter;
    private EventCounter? _connectionDuration;

    private long _connectionsStarted;
    private long _connectionsStopped;
    private long _connectionsTimedOut;
    private long _currentConnections;

    internal ConnectionEventSource() : base("Assimalign.Cohesion.Connections")
    {
    }

    [Event(eventId: 1, Level = EventLevel.Informational, Message = "{0}, Listener Id: {1}")]
    public void ListenerInitialized(ConnectionProtocol protocol, ListenerId listenerId)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            WriteEvent(1, listenerId.ToString());
        }
    }


    [Event(eventId: 2, Level = EventLevel.Informational, Message = "{0}, Listener Id: {1}, Connection Id: {2}")]
    public void ConnectionStart(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            WriteEvent(2, protocol, listenerId.ToString(), connectionId.ToString());
        }
    }


    [Event(eventId: 3, Level = EventLevel.Informational, Message = "{0}, Listener Id: {1}, Connection Id: {2}")]
    public void ConnectionStop(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            WriteEvent(3, protocol, listenerId.ToString(), connectionId.ToString());
        }
    }

    [Event(eventId: 4, Level = EventLevel.Verbose, Message = "{0}, Listener Id: {1}, Connection Id: {2}")]
    public void ConnectionFinished(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            WriteEvent(4, protocol, listenerId.ToString(), connectionId.ToString());
        }
    }

    [Event(eventId: 5, Level = EventLevel.Verbose, Message = "{0}, Listener Id: {1}, Connection Id: {2}")]
    public void ConnectionPaused(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            WriteEvent(5, protocol, listenerId.ToString(), connectionId.ToString());
        }
    }

    [Event(eventId: 6, Level = EventLevel.Verbose, Message = "{0}, Listener Id: {1}, Connection Id: {2}")]
    public void ConnectionResumed(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            WriteEvent(6, protocol, listenerId.ToString(), connectionId.ToString());
        }
    }

    [Event(eventId: 7, Level = EventLevel.Verbose, Message = "{0}, Listener Id: {1}, Connection Id: {2}")]
    public void ConnectionReset(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            WriteEvent(7, protocol, listenerId.ToString(), connectionId.ToString());
        }
    }

    [Event(eventId: 8, Level = EventLevel.Error, Message = "{0}, Listener Id: {1}, Connection Id: {2}, Message: {3}")]
    public void ConnectionError(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId, string errorMessage)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            WriteEvent(8, protocol.ToString(), listenerId.ToString(), connectionId.ToString(), errorMessage);
        }
    }



    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            // This is the convention for initializing counters in the RuntimeEventSource (lazily on the first enable command).
            // They aren't disabled afterwards...

            _connectionsStartedCounter ??= new PollingCounter("connections-started", this, () => Volatile.Read(ref _connectionsStarted))
            {
                DisplayName = "Total Connections Started",
            };
            _connectionsStoppedCounter ??= new PollingCounter("connections-stopped", this, () => Volatile.Read(ref _connectionsStopped))
            {
                DisplayName = "Total Connections Stopped",
            };
            _connectionsTimedOutCounter ??= new PollingCounter("connections-timed-out", this, () => Volatile.Read(ref _connectionsTimedOut))
            {
                DisplayName = "Total Connections Timed Out",
            };
            _currentConnectionsCounter ??= new PollingCounter("current-connections", this, () => Volatile.Read(ref _currentConnections))
            {
                DisplayName = "Current Connections",
            };

            _connectionDuration ??= new EventCounter("connections-duration", this)
            {
                DisplayName = "Average Connection Duration",
                DisplayUnits = "ms",
            };
        }
    }
}
