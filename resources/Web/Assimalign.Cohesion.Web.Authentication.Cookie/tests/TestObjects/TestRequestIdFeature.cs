using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Tests.TestObjects;

internal sealed class TestRequestIdFeature : IWebRequestIdFeature
{
    public string Name => nameof(IWebRequestIdFeature);
    private readonly ActivityTraceId _requestId = ActivityTraceId.CreateRandom();
    public int Reads { get; private set; }
    public ActivityTraceId RequestId
    {
        get
        {
            Reads++;
            return _requestId;
        }
    }
}
