namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpExtendedConnectFeature"/> implementation, backed by
/// the <c>:protocol</c> pseudo-header value the transport surfaced for the
/// current exchange (RFC 8441 / RFC 9220).
/// </summary>
internal sealed class HttpExtendedConnectFeature : IHttpExtendedConnectFeature
{
    /// <summary>
    /// Initializes a new <see cref="HttpExtendedConnectFeature"/> for the
    /// requested protocol.
    /// </summary>
    /// <param name="protocol">
    /// The requested <c>:protocol</c> value (for example <c>websocket</c>);
    /// never <see langword="null"/> or empty.
    /// </param>
    public HttpExtendedConnectFeature(string protocol)
    {
        Protocol = protocol;
    }

    /// <inheritdoc />
    public string Name => "Assimalign.Cohesion.Http.ExtendedConnect";

    /// <inheritdoc />
    public string Protocol { get; }
}
