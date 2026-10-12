using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

internal sealed class ControlPlaneRequestIdFeature : IWebRequestIdFeature
{
    private readonly ActivityTraceId _requestId = ActivityTraceId.CreateRandom();

    public string Name => nameof(ControlPlaneRequestIdFeature);

    public ActivityTraceId RequestId
    {
        get
        {
            Reads++;
            return _requestId;
        }
    }

    internal int Reads { get; private set; }

    internal string ExpectedId => _requestId.ToHexString();
}
