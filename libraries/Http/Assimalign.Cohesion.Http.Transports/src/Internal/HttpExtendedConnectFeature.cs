namespace Assimalign.Cohesion.Http.Transports.Internal;

/// <summary>
/// Transport-produced implementation of <see cref="IHttpExtendedConnectFeature"/>.
/// Installed by the HTTP/2 and HTTP/3 request paths when a request is a valid
/// extended CONNECT (RFC 8441 / RFC 9220), carrying the requested
/// <c>:protocol</c> through to the application.
/// </summary>
internal sealed class HttpExtendedConnectFeature : IHttpExtendedConnectFeature
{
    /// <summary>The feature slot name in the per-request feature collection.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.ExtendedConnect";

    /// <summary>
    /// Initializes the feature with the requested <c>:protocol</c> value.
    /// </summary>
    /// <param name="protocol">The non-empty <c>:protocol</c> token.</param>
    public HttpExtendedConnectFeature(string protocol)
    {
        Protocol = protocol;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public string Protocol { get; }
}
