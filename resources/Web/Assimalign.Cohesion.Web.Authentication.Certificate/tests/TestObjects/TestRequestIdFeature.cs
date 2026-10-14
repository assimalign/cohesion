using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Tests.TestObjects;

internal sealed class TestRequestIdFeature : IWebRequestIdFeature
{
    private readonly ActivityTraceId _requestId = ActivityTraceId.CreateRandom();

    public string Name => nameof(IWebRequestIdFeature);
    public int Reads { get; private set; }
    public string Value => _requestId.ToHexString();

    public ActivityTraceId RequestId
    {
        get
        {
            Reads++;
            return _requestId;
        }
    }
}
