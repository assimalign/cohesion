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
    }

    protected bool IsSecure { get; }
    protected IConnection Connection { get; }
    protected Stream Stream { get; }
    protected HttpConnectionInfo ConnectionInfo { get; }

    public override EndPoint? LocalEndPoint => Connection.LocalEndPoint;
    public override EndPoint? RemoteEndPoint => Connection.RemoteEndPoint;

    protected HttpScheme GetScheme()
    {
        return IsSecure ? HttpScheme.Https : HttpScheme.Http;
    }
}
