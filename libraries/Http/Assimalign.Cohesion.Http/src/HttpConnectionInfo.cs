using System.Net;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides concrete HTTP connection metadata.
/// </summary>
public sealed class HttpConnectionInfo : IHttpConnectionInfo
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
    public HttpConnectionInfo(EndPoint? localEndPoint = null, EndPoint? remoteEndPoint = null, bool isSecure = false)
    {
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        IsSecure = isSecure;
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
    public bool IsSecure { get; init; }
}
