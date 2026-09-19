using System;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections;

/// <summary>Reports driver lifecycle events to the shared Connections diagnostic source.</summary>
// Deviates from the repo interface-first rule per owner-authorized Phase 19 decision:
// diagnostic reporting is a stateless operation; the EventSource and its lifetime remain internal.
public static class ConnectionDiagnostics
{
    /// <summary>Reports that a listener has bound its endpoint.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    public static void ListenerInitialized(ConnectionProtocol protocol, ListenerId listenerId) =>
        ConnectionEventSource.Log.ListenerInitialized(protocol, listenerId);

    /// <summary>Reports that a connection has started.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    /// <param name="connectionId">The connection correlation identifier.</param>
    public static void ConnectionStart(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId) =>
        ConnectionEventSource.Log.ConnectionStart(protocol, listenerId, connectionId);

    /// <summary>Reports that a connection has stopped.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    /// <param name="connectionId">The connection correlation identifier.</param>
    public static void ConnectionStop(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId) =>
        ConnectionEventSource.Log.ConnectionStop(protocol, listenerId, connectionId);

    /// <summary>Reports that a connection receive loop has finished.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    /// <param name="connectionId">The connection correlation identifier.</param>
    public static void ConnectionFinished(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId) =>
        ConnectionEventSource.Log.ConnectionFinished(protocol, listenerId, connectionId);

    /// <summary>Reports that receive back-pressure has paused a connection.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    /// <param name="connectionId">The connection correlation identifier.</param>
    public static void ConnectionPaused(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId) =>
        ConnectionEventSource.Log.ConnectionPaused(protocol, listenerId, connectionId);

    /// <summary>Reports that a connection has resumed after back-pressure.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    /// <param name="connectionId">The connection correlation identifier.</param>
    public static void ConnectionResumed(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId) =>
        ConnectionEventSource.Log.ConnectionResumed(protocol, listenerId, connectionId);

    /// <summary>Reports that a connection was reset by its peer.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    /// <param name="connectionId">The connection correlation identifier.</param>
    public static void ConnectionReset(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId) =>
        ConnectionEventSource.Log.ConnectionReset(protocol, listenerId, connectionId);

    /// <summary>Reports a connection error.</summary>
    /// <param name="protocol">The actual wire protocol.</param>
    /// <param name="listenerId">The originating listener, or the default value for a factory connection.</param>
    /// <param name="connectionId">The connection correlation identifier.</param>
    /// <param name="errorMessage">The diagnostic error description.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errorMessage"/> is null.</exception>
    public static void ConnectionError(ConnectionProtocol protocol, ListenerId listenerId, ConnectionId connectionId, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);
        ConnectionEventSource.Log.ConnectionError(protocol, listenerId, connectionId, errorMessage);
    }

}
