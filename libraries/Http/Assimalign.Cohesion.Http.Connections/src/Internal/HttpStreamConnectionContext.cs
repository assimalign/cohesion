using System.IO;
using System.Net;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Shared base for HTTP connection contexts that parse a single reliable, ordered byte
/// stream (HTTP/1.1 and HTTP/2). The wrapped <see cref="IConnection"/> is the duplex pipe;
/// the <see cref="Stream"/> adapter is created once over it for the stream-based parsers.
/// </summary>
internal abstract class HttpStreamConnectionContext : HttpConnectionContext
{
    protected HttpStreamConnectionContext(IConnection connection, bool isSecure)
    {
        Connection = connection;
        Stream = connection.AsStream();
        ConnectionInfo = new HttpConnectionInfo(connection.LocalEndPoint, connection.RemoteEndPoint);
        IsSecure = isSecure;
        TlsConnection = HttpTlsConnectionFeature.From(connection);
    }

    protected bool IsSecure { get; }
    protected IConnection Connection { get; }
    protected Stream Stream { get; }
    protected HttpConnectionInfo ConnectionInfo { get; }

    /// <summary>
    /// The TLS session of this connection, attached to every exchange it carries, or
    /// <see langword="null"/> when the connection does not report one (see
    /// <see cref="HttpTlsConnectionFeature.From(object)"/>).
    /// </summary>
    protected HttpTlsConnectionFeature? TlsConnection { get; }

    /// <summary>
    /// Attaches the connection's TLS session to an exchange, before any response interceptor or
    /// application code observes the exchange's features.
    /// </summary>
    /// <param name="context">The exchange the connection just produced.</param>
    protected void AttachTlsConnection(TransportHttpContext context)
    {
        if (TlsConnection is not null)
        {
            context.Features.Set(TlsConnection);
        }
    }

    public override EndPoint? LocalEndPoint => Connection.LocalEndPoint;
    public override EndPoint? RemoteEndPoint => Connection.RemoteEndPoint;

    protected HttpScheme GetScheme()
    {
        return IsSecure ? HttpScheme.Https : HttpScheme.Http;
    }
}
