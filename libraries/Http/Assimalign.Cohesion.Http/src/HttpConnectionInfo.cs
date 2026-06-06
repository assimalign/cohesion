using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides concrete HTTP connection metadata.
/// </summary>
public class HttpConnectionInfo : IHttpConnectionInfo
{
    /// <summary>
    /// Gets an empty connection info instance.
    /// </summary>
    public static HttpConnectionInfo Empty { get; } = new();

    /// <summary>
    /// Initializes a new connection info instance.
    /// </summary>
    /// <param name="localEndPoint">The local endpoint.</param>
    /// <param name="remoteEndPoint">The remote endpoint.</param>
    /// <param name="isSecure">Indicates whether the connection is secured.</param>
    public HttpConnectionInfo(EndPoint? localEndPoint = null, EndPoint? remoteEndPoint = null)
    {
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint { get; init; }

    /// <inheritdoc />
    public EndPoint? RemoteEndPoint { get; init; }

    /// <inheritdoc />
    public IPAddress? LocalIp => (LocalEndPoint as IPEndPoint)?.Address;

    /// <inheritdoc />
    public IPAddress? RemoteIp => (RemoteEndPoint as IPEndPoint)?.Address;

    /// <inheritdoc />
    public int LocalPort => (LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <inheritdoc />
    public int RemotePort => (RemoteEndPoint as IPEndPoint)?.Port ?? 0;

    /// <inheritdoc />
    public virtual CancellationToken ConnectionAborted { get; } = CancellationToken.None;

    /// <inheritdoc />
    public virtual void Abort()
    {
        AbortAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public virtual ValueTask AbortAsync()
    {
        return ValueTask.CompletedTask;
    }
}
