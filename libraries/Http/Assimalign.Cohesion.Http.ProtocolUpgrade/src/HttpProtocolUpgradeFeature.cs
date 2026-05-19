using System;

namespace Assimalign.Cohesion.Http;

public sealed class HttpProtocolUpgradeFeature : IHttpProtocolUpgradeFeature
{
    public string Name => throw new NotImplementedException();
    public IHttpProtocolUpgrade Upgrade => throw new NotImplementedException();

}
