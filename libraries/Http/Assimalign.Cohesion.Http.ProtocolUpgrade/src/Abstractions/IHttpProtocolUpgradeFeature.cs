namespace Assimalign.Cohesion.Http;

public interface IHttpProtocolUpgradeFeature : IHttpFeature
{
    IHttpProtocolUpgrade Upgrade { get; }
}
