namespace Assimalign.Cohesion.Http;

/// <summary>
/// Exchange feature that exposes the protocol-upgrade capability
/// (<see cref="IHttpProtocolUpgrade"/>) for the current request. Installed at parse time by the
/// interceptor pair this package registers on the server transport
/// (<see cref="HttpProtocolUpgrade.CreateRequestInterceptor"/> /
/// <see cref="HttpProtocolUpgrade.CreateResponseInterceptor"/>) — only for exchanges whose
/// request head matched a transition signal. Ordinary exchanges carry no such feature, which is
/// how <c>context.Upgrade</c> reads <see langword="null"/> for them.
/// </summary>
public interface IHttpProtocolUpgradeFeature : IHttpFeature
{
    /// <summary>
    /// Gets the protocol upgrade for the current exchange.
    /// </summary>
    IHttpProtocolUpgrade Upgrade { get; }
}
