namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpProtocolUpgradeFeature"/> implementation. Installed by
/// <see cref="HttpProtocolUpgradeInterceptor"/>'s response hook for exchanges whose request head
/// matched a transition signal and whose transport offered its connection-takeover capability;
/// holds the single-shot <see cref="Http1ProtocolUpgrade"/> that <c>context.Upgrade</c> surfaces.
/// Ordinary exchanges carry no such feature, so the accessor reads <see langword="null"/>.
/// </summary>
internal sealed class HttpProtocolUpgradeFeature : IHttpProtocolUpgradeFeature
{
    /// <summary>The name under which the protocol-upgrade feature is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.ProtocolUpgrade";

    /// <summary>
    /// Initializes the feature around the exchange's upgrade.
    /// </summary>
    /// <param name="upgrade">The upgrade built for the exchange.</param>
    public HttpProtocolUpgradeFeature(IHttpProtocolUpgrade upgrade)
    {
        Upgrade = upgrade;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public IHttpProtocolUpgrade Upgrade { get; }
}
