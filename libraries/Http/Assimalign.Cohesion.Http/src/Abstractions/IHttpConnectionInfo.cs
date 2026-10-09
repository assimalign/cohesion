using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Describes the local and remote endpoints associated with an HTTP request.
/// </summary>
/// <remarks>
/// <para>
/// This represents the transport connection.
/// </para>
/// <para>
/// A transport may publish further facts about the connection as <em>facets</em>: additional
/// interfaces the object it hands out also implements, found with a type test. The server transport
/// (<c>Assimalign.Cohesion.Http.Connections</c>) publishes what a TLS handshake negotiated as the
/// Connections contracts library's <c>ITlsConnectionInfo</c>, which the
/// <c>context.TlsConnection</c> accessor (<c>Assimalign.Cohesion.Http.Tls</c>) reads. A facet keeps
/// this interface free of members only some connections can answer. Code that wraps an
/// <see cref="IHttpContext"/> therefore forwards the inner context's connection info rather than
/// building a new object, which would hide the facets.
/// </para>
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
    /// Forcibly aborts the connection, causing the <see cref="ConnectionAborted"/> token to be triggered.
    /// </summary>
    void Abort();

    /// <summary>
    /// Asynchronously and forcibly aborts the connection, causing the <see cref="ConnectionAborted"/> token
    /// to be triggered.
    /// </summary>
    /// <returns>A task that completes when the abort has been carried out.</returns>
    ValueTask AbortAsync();
}