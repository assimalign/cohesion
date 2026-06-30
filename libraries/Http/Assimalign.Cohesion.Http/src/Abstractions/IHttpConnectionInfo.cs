using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Describes the local and remote endpoints associated with an HTTP request.
/// </summary>
/// <remarks>
/// This representst the transport connection.
/// </remarks>
public interface IHttpConnectionInfo
{
    /// <summary>
    /// Gets the remote port for the active connection.
    /// </summary>
    int RemotePort { get; }

    /// <summary>
    /// Gets the remote IP address for the active connection.
    /// </summary>
    IPAddress? RemoteIp { get; }

    /// <summary>
    /// Gets the remote endpoint for the active connection.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the local port for the active connection.
    /// </summary>
    int LocalPort { get; }

    /// <summary>
    /// Gets the local IP address for the active connection.
    /// </summary>
    IPAddress? LocalIp { get; }

    /// <summary>
    /// Gets the local endpoint for the active connection.
    /// </summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// 
    /// </summary>
    CancellationToken ConnectionAborted { get; }

    /// <summary>
    /// Foreciblly aborts the connection, causing the <see cref="ConnectionAborted"/> token to be triggered.
    /// </summary>
    void Abort();

    /// <summary>
    /// Foreciblly aborts the connection, causing the <see cref="ConnectionAborted"/> token to be triggered.
    /// </summary>
    /// <returns></returns>
    ValueTask AbortAsync();
}