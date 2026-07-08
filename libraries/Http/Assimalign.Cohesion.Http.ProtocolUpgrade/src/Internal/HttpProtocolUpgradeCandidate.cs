namespace Assimalign.Cohesion.Http;

/// <summary>
/// Parse-time marker recording that the request head matched an HTTP/1.1 transition signal
/// (RFC 9110 §7.8 upgrade or §9.3.6 <c>CONNECT</c>). Installed by
/// <see cref="HttpProtocolUpgradeInterceptor"/>'s request hook and consumed — removed and
/// replaced with the public <see cref="IHttpProtocolUpgradeFeature"/> — by its response hook,
/// where the transport's connection-takeover capability becomes available. The two hooks are
/// stateless shared instances, so this feature is how detection state crosses from the request
/// seam to the response seam within one exchange.
/// </summary>
internal sealed class HttpProtocolUpgradeCandidate : IHttpFeature
{
    /// <summary>The name under which the candidate marker is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.ProtocolUpgrade.Candidate";

    public HttpProtocolUpgradeCandidate(HttpProtocolUpgradeKind kind, string? protocol)
    {
        Kind = kind;
        Protocol = protocol;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <summary>The detected transition kind (never <see cref="HttpProtocolUpgradeKind.None"/>).</summary>
    public HttpProtocolUpgradeKind Kind { get; }

    /// <summary>The requested <c>Upgrade</c> protocol token, or <see langword="null"/> for CONNECT.</summary>
    public string? Protocol { get; }
}
