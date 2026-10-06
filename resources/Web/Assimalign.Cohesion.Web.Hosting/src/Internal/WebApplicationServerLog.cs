using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Logging;

// Disambiguate from System.Net.HttpVersion, pulled in by the System.Net using for the endpoints.
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// The default server's own diagnostics, written through the application's logger factory: a
/// listener that cannot be bound, an accept loop that faults, a connection that ends in a fault, and
/// a stop whose budget ran out with work still in flight.
/// </summary>
/// <remarks>
/// <para>
/// Every entry is written under <see cref="Category"/> with the attribute names declared here, and
/// none carries request or response content — no header value, no body, no path or query. A
/// connection is identified by its id, its endpoints, and its protocol version only.
/// </para>
/// <para>
/// The levels are deliberate (Web.Hosting docs/DESIGN.md, "Diagnostics"): a bind or accept-loop
/// failure is <see cref="LogLevel.Critical"/>, because the server cannot serve; a connection fault is
/// <see cref="LogLevel.Error"/> when it is a defect the server isolated, and
/// <see cref="LogLevel.Debug"/> when the peer or the network ended the connection, or the server
/// aborted it itself; a drain the budget cut short is <see cref="LogLevel.Warning"/>, because the
/// requests it cancelled got no response.
/// </para>
/// <para>
/// A logger that throws never changes what the server does: every write is isolated.
/// </para>
/// </remarks>
internal sealed class WebApplicationServerLog
{
    /// <summary>The category every entry is written under.</summary>
    public const string Category = "Assimalign.Cohesion.Web.Hosting.WebApplicationServer";

    /// <summary>The faulted connection's transport id. String.</summary>
    public const string ConnectionIdAttribute = "connection.id";

    /// <summary>The local address of the faulted connection (OpenTelemetry <c>network.local.address</c>). String.</summary>
    public const string LocalAddressAttribute = "network.local.address";

    /// <summary>The local port of the faulted connection (OpenTelemetry <c>network.local.port</c>). Int.</summary>
    public const string LocalPortAttribute = "network.local.port";

    /// <summary>The peer address of the faulted connection (OpenTelemetry <c>network.peer.address</c>). String.</summary>
    public const string PeerAddressAttribute = "network.peer.address";

    /// <summary>The peer port of the faulted connection (OpenTelemetry <c>network.peer.port</c>). Int.</summary>
    public const string PeerPortAttribute = "network.peer.port";

    /// <summary>
    /// The HTTP version the faulted connection served (OpenTelemetry <c>network.protocol.version</c>:
    /// <c>1.1</c>, <c>2</c>, or <c>3</c>). Absent when the connection faulted before its first exchange.
    /// String.
    /// </summary>
    public const string ProtocolVersionAttribute = "network.protocol.version";

    /// <summary>The protocols the listener that failed to bind serves, e.g. <c>HTTP/1.1, HTTP/2</c>. String.</summary>
    public const string ListenerProtocolsAttribute = "http.server.listener.protocols";

    /// <summary>How many connections were still in flight when the stop's budget ran out. Int.</summary>
    public const string DrainConnectionsAttribute = "http.server.drain.connections";

    /// <summary>How many exchanges were still in flight when the stop's budget ran out. Int.</summary>
    public const string DrainExchangesAttribute = "http.server.drain.exchanges";

    /// <summary>How long the drain ran before its budget ran out. <see cref="TimeSpan"/>.</summary>
    public const string DrainDurationAttribute = "http.server.drain.duration";

    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes the log over <paramref name="logger"/>; <see langword="null"/> writes nothing.
    /// </summary>
    public WebApplicationServerLog(ILogger? logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The listener could not be bound, so the server, and the application, cannot start.
    /// </summary>
    public void BindFailed(HttpProtocol protocols, Exception exception)
    {
        string serving = FormatProtocols(protocols);

        Write(
            LogLevel.Critical,
            $"The web application server could not bind its listener ({serving}), so it cannot start.",
            exception,
            new Dictionary<string, object?>
            {
                [ListenerProtocolsAttribute] = serving,
            });
    }

    /// <summary>
    /// The accept loop faulted: the server accepts no further connection, although it still runs.
    /// </summary>
    public void AcceptLoopFaulted(Exception exception)
    {
        Write(
            LogLevel.Critical,
            "The web application server's accept loop faulted, and the server accepts no further connections until it is restarted.",
            exception,
            attributes: null);
    }

    /// <summary>
    /// A connection ended in a fault and was aborted; the server keeps serving the others.
    /// </summary>
    /// <param name="connection">The faulted connection.</param>
    /// <param name="context">Its opened context, or <see langword="null"/> when it faulted before or while opening.</param>
    /// <param name="version">The version of the exchanges it carried, or <see cref="HttpVersion.Unknown"/> before the first one.</param>
    /// <param name="exception">The fault.</param>
    /// <param name="aborted">Whether the server had aborted its drain, and so the connection, before the fault.</param>
    public void ConnectionFaulted(IHttpConnection connection, IHttpConnectionContext? context, HttpVersion version, Exception exception, bool aborted)
    {
        LogLevel level = aborted || IsTransportFailure(exception) ? LogLevel.Debug : LogLevel.Error;

        if (_logger is null || !IsEnabled(level))
        {
            return;
        }

        Dictionary<string, object?> attributes = new(StringComparer.Ordinal);
        string connectionId = connection.Id.ToString();
        attributes[ConnectionIdAttribute] = connectionId;
        AddEndPoint(attributes, context?.LocalEndPoint, LocalAddressAttribute, LocalPortAttribute);
        AddEndPoint(attributes, context?.RemoteEndPoint, PeerAddressAttribute, PeerPortAttribute);

        string? protocolVersion = version switch
        {
            HttpVersion.Http11 => "1.1",
            HttpVersion.Http20 => "2",
            HttpVersion.Http30 => "3",
            _ => null,
        };

        if (protocolVersion is not null)
        {
            attributes[ProtocolVersionAttribute] = protocolVersion;
        }

        Write(
            level,
            $"Connection {connectionId} (HTTP/{protocolVersion ?? "unknown"}, peer {context?.RemoteEndPoint?.ToString() ?? "unknown"}) ended in a fault and was aborted.",
            exception,
            attributes);
    }

    /// <summary>
    /// The stop's budget ran out with work still in flight: what is left is cancelled.
    /// </summary>
    public void DrainTimedOut(int connections, int exchanges, TimeSpan elapsed)
    {
        Write(
            LogLevel.Warning,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The web application server's stop budget ran out after {elapsed.TotalSeconds:0.###} s with {connections} connection(s) and {exchanges} exchange(s) still in flight; they are cancelled and their connections aborted."),
            exception: null,
            new Dictionary<string, object?>
            {
                [DrainConnectionsAttribute] = connections,
                [DrainExchangesAttribute] = exchanges,
                [DrainDurationAttribute] = elapsed,
            });
    }

    /// <summary>
    /// Whether <paramref name="exception"/> only says that the peer or the network ended the
    /// connection — routine on any endpoint, and not actionable.
    /// </summary>
    private static bool IsTransportFailure(Exception exception)
    {
        return exception is IOException or SocketException or ConnectionException;
    }

    /// <summary>
    /// Renders a protocol set as <c>HTTP/1.1, HTTP/2, HTTP/3</c>. The enum's own names are not stable:
    /// <see cref="HttpProtocol.Http1"/> aliases <see cref="HttpProtocol.Http11"/>, and so on.
    /// </summary>
    private static string FormatProtocols(HttpProtocol protocols)
    {
        List<string> names = new(3);

        if (protocols.HasFlag(HttpProtocol.Http11))
        {
            names.Add("HTTP/1.1");
        }

        if (protocols.HasFlag(HttpProtocol.Http20))
        {
            names.Add("HTTP/2");
        }

        if (protocols.HasFlag(HttpProtocol.Http30))
        {
            names.Add("HTTP/3");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static void AddEndPoint(Dictionary<string, object?> attributes, EndPoint? endPoint, string addressAttribute, string portAttribute)
    {
        switch (endPoint)
        {
            case IPEndPoint ip:
                attributes[addressAttribute] = ip.Address.ToString();
                attributes[portAttribute] = ip.Port;
                break;
            case not null:
                attributes[addressAttribute] = endPoint.ToString();
                break;
        }
    }

    private bool IsEnabled(LogLevel level)
    {
        try
        {
            return _logger!.IsEnabled(level);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: the logger is
        // composed by the application, and a failing one must not change what the server does.
        catch (Exception)
        {
            return false;
        }
    }

    private void Write(LogLevel level, string message, Exception? exception, IReadOnlyDictionary<string, object?>? attributes)
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            if (_logger.IsEnabled(level))
            {
                _logger.Log(new LoggerEntry(level, Category, message, exception, attributes));
            }
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: the logger is
        // composed by the application, and a failing one must not change what the server does —
        // these writes sit inside its fault-isolation boundaries and its startup failure path.
        catch (Exception)
        {
        }
    }
}
