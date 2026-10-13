using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using EndPoint = System.Net.EndPoint;
using System.Net.Sockets;
using System.Threading;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.Hosting")]
internal sealed class WebServerEventSource : EventSource
{
    public static readonly WebServerEventSource Log = new();

    private long _connectionFaults;
    private long _connectionCapWaits;
    private long _refusedResponseHeads;
    private long _rejectedCredentials;
    private readonly EventThrottle _headThrottle = new();
    private readonly EventThrottle _telemetryThrottle = new();
    private readonly EventThrottle _credentialThrottle = new();
    private readonly EventThrottle _malformedThrottle = new();
    private PollingCounter? _connectionFaultsCounter;
    private PollingCounter? _connectionCapWaitsCounter;
    private PollingCounter? _refusedResponseHeadsCounter;
    private PollingCounter? _rejectedCredentialsCounter;

    private WebServerEventSource() { }

    internal long ConnectionFaults => Interlocked.Read(ref _connectionFaults);
    internal long ConnectionCapWaits => Interlocked.Read(ref _connectionCapWaits);
    internal long RefusedResponseHeads => Interlocked.Read(ref _refusedResponseHeads);
    internal long RejectedCredentials => Interlocked.Read(ref _rejectedCredentials);

    [NonEvent]
    public void ServerBound(long serverId, IHttpConnectionListener listener, int maxConcurrentConnections)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            IReadOnlyList<EndPoint> boundEndPoints = listener.EndPoints;
            string[] endPoints = new string[boundEndPoints.Count];
            for (int index = 0; index < endPoints.Length; index++)
            {
                endPoints[index] = boundEndPoints[index].ToString() ?? string.Empty;
            }
            ServerBound(serverId, FormatProtocols(listener.Protocols), string.Join(", ", endPoints), maxConcurrentConnections);
        }
    }

    [NonEvent]
    public void BindFailed(long serverId, HttpProtocol protocols, Exception exception)
    {
        if (IsEnabled(EventLevel.Critical, EventKeywords.None))
        {
            BindFailed(serverId, FormatProtocols(protocols), exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [NonEvent]
    public void AcceptLoopFaulted(long serverId, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            AcceptLoopFaulted(serverId, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [NonEvent]
    public void ConnectionEnded(long serverId, IHttpConnection connection, IHttpConnectionContext? context,
        HttpVersion version, Exception exception, bool aborted, ref bool faultCounted)
    {
        bool peerEnded = aborted || exception is IOException or SocketException or ConnectionException;
        if (!peerEnded && !faultCounted)
        {
            faultCounted = true;
            Interlocked.Increment(ref _connectionFaults);
        }

        if (IsEnabled(peerEnded ? EventLevel.Verbose : EventLevel.Error, EventKeywords.None))
        {
            string id = connection.Id.ToString();
            string local = context?.LocalEndPoint?.ToString() ?? string.Empty;
            string remote = context?.RemoteEndPoint?.ToString() ?? string.Empty;
            string protocol = FormatVersion(version);
            string type = exception.GetType().FullName ?? exception.GetType().Name;
            if (peerEnded)
            {
                ConnectionEndedByPeer(serverId, id, local, remote, protocol, type, exception.Message);
            }
            else
            {
                ConnectionFaulted(serverId, id, local, remote, protocol, type, exception.Message);
            }
        }
    }

    [NonEvent]
    public void Draining(long serverId, int connections, int exchanges)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ServerDraining(serverId, connections, exchanges);
        }
    }

    [NonEvent]
    public void DrainTimedOut(long serverId, int connections, int exchanges, TimeSpan elapsed)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            DrainTimedOut(serverId, connections, exchanges, elapsed.TotalMilliseconds);
        }
    }

    [NonEvent]
    public void AbortGraceExpired(long serverId, int exchanges, TimeSpan gracePeriod)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            AbortGraceExpired(serverId, exchanges, gracePeriod.TotalMilliseconds);
        }
    }

    [NonEvent]
    public void ServerStopped(long serverId, TimeSpan elapsed, bool aborted)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ServerStopped(serverId, elapsed.TotalMilliseconds, aborted);
        }
    }

    [NonEvent]
    public void ResponseHeadRefused(long serverId, IHttpContext exchange, Exception exception)
    {
        Interlocked.Increment(ref _refusedResponseHeads);
        if (IsEnabled(EventLevel.Error, EventKeywords.None) &&
            _headThrottle.TryEmit(out int suppressed))
        {
            ResponseHeadRefused(serverId, GetRequestId(exchange), FormatVersion(exchange.Version),
                exchange.Features.Get<IWebEndpointFeature>()?.RouteTemplate ?? string.Empty,
                "The HTTP transport refused an invalid response field.", suppressed);
        }
    }

    [NonEvent]
    public void CompletionCallbackFailed(long serverId, IHttpContext exchange, Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            CompletionCallbackFailed(serverId, GetRequestId(exchange),
                exchange.Features.Get<IWebEndpointFeature>()?.RouteTemplate ?? string.Empty,
                exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [NonEvent]
    public void ExchangeIsolationEscaped(Exception exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            ExchangeIsolationEscaped(exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [NonEvent]
    public void ConnectionCapWait(long serverId, int maxConcurrentConnections)
    {
        Interlocked.Increment(ref _connectionCapWaits);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ConnectionCapReached(serverId, maxConcurrentConnections);
        }
    }

    [NonEvent]
    public void ControlPlaneDisabled(string? resourceName, IResourceControlPlane controlPlane)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ControlPlaneDisabled(resourceName ?? string.Empty, string.Join(", ", controlPlane.ObservedEndpoints.Keys));
        }
    }

    [NonEvent]
    public void TelemetryListenerFaulted(Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) &&
            _telemetryThrottle.TryEmit(out int suppressed))
        {
            TelemetryListenerFaulted(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, suppressed);
        }
    }

    [NonEvent]
    internal void CredentialRejected(ResourceContext resource, IHttpContext context,
        ResourceCredentialStatus status, CredentialRejectionReason reason)
    {
        Interlocked.Increment(ref _rejectedCredentials);
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _credentialThrottle.TryEmit(out int suppressed))
        {
            CredentialRejected(resource.ResourceName ?? string.Empty, GetRequestId(context), status.ToString(),
                ReasonCode(reason), Route(context.Request.Path.Value),
                context.ConnectionInfo.RemoteEndPoint?.ToString() ?? string.Empty, suppressed);
        }
    }

    [NonEvent]
    internal void CommandMalformed(ResourceContext resource, IHttpContext context, string phase, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None) && _malformedThrottle.TryEmit(out int suppressed))
        {
            CommandMalformed(resource.ResourceName ?? string.Empty, GetRequestId(context),
                context.Request.Method == HttpMethod.Delete ? "DELETE" : "POST", phase,
                exception.GetType().FullName ?? exception.GetType().Name, suppressed);
        }
    }

    [NonEvent]
    internal void StopAccepted(ResourceContext resource, IHttpContext context, bool deferred)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            StopAccepted(resource.ResourceName ?? string.Empty, GetRequestId(context), deferred);
        }
    }

    [NonEvent]
    private static string GetRequestId(IHttpContext exchange)
        => exchange.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty;

    [NonEvent]
    private static string FormatVersion(HttpVersion version) => version switch
    {
        HttpVersion.Http11 => "1.1",
        HttpVersion.Http20 => "2",
        HttpVersion.Http30 => "3",
        _ => string.Empty,
    };

    [NonEvent]
    private static string FormatProtocols(HttpProtocol protocols)
    {
        List<string> names = new(3);
        if ((protocols & HttpProtocol.Http11) != 0)
        {
            names.Add("HTTP/1.1");
        }
        if ((protocols & HttpProtocol.Http20) != 0)
        {
            names.Add("HTTP/2");
        }
        if ((protocols & HttpProtocol.Http30) != 0)
        {
            names.Add("HTTP/3");
        }
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    [NonEvent]
    private static string Route(string path) => path switch
    {
        "/cohesion/v1/stop" => "stop",
        "/cohesion/v1/commands" => "commands",
        "/cohesion/v1/endpoints" => "endpoints",
        "/cohesion/v1/healthz" => "healthz",
        "/cohesion/v1/readyz" => "readyz",
        "/cohesion/v1/livez" => "livez",
        _ => "unknown",
    };

    [NonEvent]
    private static string ReasonCode(CredentialRejectionReason reason) => reason switch
    {
        CredentialRejectionReason.MissingCredential => "missing-credential",
        CredentialRejectionReason.UnsupportedScheme => "unsupported-scheme",
        CredentialRejectionReason.InvalidToken => "invalid-token",
        CredentialRejectionReason.AudienceMismatch => "audience-mismatch",
        CredentialRejectionReason.CallerMismatch => "caller-mismatch",
        CredentialRejectionReason.VerifierRejected => "verifier-rejected",
        CredentialRejectionReason.NonLoopbackPeer => "non-loopback-peer",
        _ => "verifier-rejected",
    };

    [Event(1, Level = EventLevel.Informational, Message = "Server {0} bound ({1}) at {2}; connection cap {3}")]
    private void ServerBound(long serverId, string protocols, string endPoints, int maxConcurrentConnections)
        => WriteEvent(1, serverId, protocols, endPoints, maxConcurrentConnections);

    [Event(2, Level = EventLevel.Critical, Message = "Server {0} could not bind ({1}): {2}: {3}")]
    private void BindFailed(long serverId, string protocols, string exceptionType, string exceptionMessage)
        => WriteEvent(2, serverId, protocols, exceptionType, exceptionMessage);

    [Event(3, Level = EventLevel.Error, Message = "Server {0} accept loop faulted: {1}: {2}")]
    private void AcceptLoopFaulted(long serverId, string exceptionType, string exceptionMessage)
        => WriteEvent(3, serverId, exceptionType, exceptionMessage);

    [Event(4, Level = EventLevel.Error, Message = "Server {0} connection {1} ({2} to {3}, HTTP/{4}) faulted: {5}: {6}")]
    private void ConnectionFaulted(long serverId, string connectionId, string localEndPoint, string remoteEndPoint, string protocolVersion, string exceptionType, string exceptionMessage)
        => WriteEvent(4, serverId, connectionId, localEndPoint, remoteEndPoint, protocolVersion, exceptionType, exceptionMessage);

    [Event(5, Level = EventLevel.Verbose, Message = "Server {0} connection {1} ({2} to {3}, HTTP/{4}) ended by peer or abort: {5}: {6}")]
    private void ConnectionEndedByPeer(long serverId, string connectionId, string localEndPoint, string remoteEndPoint, string protocolVersion, string exceptionType, string exceptionMessage)
        => WriteEvent(5, serverId, connectionId, localEndPoint, remoteEndPoint, protocolVersion, exceptionType, exceptionMessage);

    [Event(6, Level = EventLevel.Informational, Message = "Server {0} draining {1} connections and {2} exchanges")]
    private void ServerDraining(long serverId, int connections, int exchanges)
        => WriteEvent(6, new EventSourcePrimitive[] { serverId, connections, exchanges });

    [Event(7, Level = EventLevel.Warning, Message = "Server {0} drain timed out with {1} connections and {2} exchanges after {3} ms")]
    private void DrainTimedOut(long serverId, int connections, int exchanges, double elapsedMilliseconds)
        => WriteEvent(7, serverId, connections, exchanges, elapsedMilliseconds);

    [Event(8, Level = EventLevel.Warning, Message = "Server {0} abort grace expired with {1} exchanges after {2} ms")]
    private void AbortGraceExpired(long serverId, int exchanges, double gracePeriodMilliseconds)
        => WriteEvent(8, serverId, exchanges, gracePeriodMilliseconds);

    [Event(9, Level = EventLevel.Informational, Message = "Server {0} stopped after {1} ms; aborted: {2}")]
    private void ServerStopped(long serverId, double elapsedMilliseconds, bool aborted)
        => WriteEvent(9, serverId, elapsedMilliseconds, aborted);

    [Event(10, Level = EventLevel.Error, Message = "Server {0} refused response head for request {1} (HTTP/{2}, route {3}): {4}; {5} suppressed")]
    private void ResponseHeadRefused(long serverId, string requestId, string protocolVersion, string routeTemplate, string exceptionMessage, int suppressedCount)
        => WriteEvent(10, serverId, requestId, protocolVersion, routeTemplate, exceptionMessage, suppressedCount);

    [Event(11, Level = EventLevel.Error, Message = "Server {0} completion callback failed for request {1}, route {2}: {3}: {4}")]
    private void CompletionCallbackFailed(long serverId, string requestId, string routeTemplate, string exceptionType, string exceptionMessage)
        => WriteEvent(11, serverId, requestId, routeTemplate, exceptionType, exceptionMessage);

    [Event(12, Level = EventLevel.Error, Message = "Exchange isolation escaped: {0}: {1}")]
    private void ExchangeIsolationEscaped(string exceptionType, string exceptionMessage)
        => WriteEvent(12, exceptionType, exceptionMessage);

    [Event(13, Level = EventLevel.Verbose, Message = "Server {0} reached its connection cap {1}")]
    private void ConnectionCapReached(long serverId, int maxConcurrentConnections)
        => WriteEvent(13, new EventSourcePrimitive[] { serverId, maxConcurrentConnections });

    [Event(14, Level = EventLevel.Warning, Message = "Resource {0} control plane disabled; observed endpoints: {1}")]
    private void ControlPlaneDisabled(string resourceName, string observedEndpoints)
        => WriteEvent(14, resourceName, observedEndpoints);

    [Event(15, Level = EventLevel.Warning, Message = "Telemetry listener failed: {0}: {1}; {2} suppressed")]
    private void TelemetryListenerFaulted(string exceptionType, string exceptionMessage, int suppressedCount)
        => WriteEvent(15, exceptionType, exceptionMessage, suppressedCount);

    [Event(16, Level = EventLevel.Warning, Message = "Resource {0} rejected a credential ({2}, {3}) on {4}; suppressed {6}")]
    private void CredentialRejected(string resourceName, string requestId, string status, string reason,
        string route, string remoteEndPoint, int suppressedCount)
        => WriteEvent(16, resourceName, requestId, status, reason, route, remoteEndPoint, suppressedCount);

    [Event(17, Level = EventLevel.Warning, Message = "Resource {0} refused malformed {2} command in {3}: {4}; suppressed {5}")]
    private void CommandMalformed(string resourceName, string requestId, string httpMethod, string phase,
        string exceptionType, int suppressedCount)
        => WriteEvent(17, resourceName, requestId, httpMethod, phase, exceptionType, suppressedCount);

    [Event(18, Level = EventLevel.Informational, Message = "Resource {0} accepted stop; deferred {2}")]
    private void StopAccepted(string resourceName, string requestId, bool deferred)
        => WriteEvent(18, resourceName, requestId, deferred);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _connectionFaultsCounter ??= new PollingCounter("connection-faults", this, () => ConnectionFaults) { DisplayName = "Faulted connections" };
            _connectionCapWaitsCounter ??= new PollingCounter("connection-cap-waits", this, () => ConnectionCapWaits) { DisplayName = "Connection cap waits" };
            _refusedResponseHeadsCounter ??= new PollingCounter("refused-response-heads", this, () => RefusedResponseHeads) { DisplayName = "Refused response heads" };
            _rejectedCredentialsCounter ??= new PollingCounter("rejected-credentials", this,
                () => RejectedCredentials) { DisplayName = "Rejected Credentials" };
        }
    }
}
