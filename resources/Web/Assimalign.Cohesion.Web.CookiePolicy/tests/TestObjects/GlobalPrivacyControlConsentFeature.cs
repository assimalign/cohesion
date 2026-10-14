using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;

/// <summary>
/// A consent feature an application supplies in place of the policy's own: it withholds non-essential
/// cookies from a request that carries the Global Privacy Control signal (<c>Sec-GPC: 1</c>).
/// </summary>
internal sealed class GlobalPrivacyControlConsentFeature : ICookieConsentFeature
{
    private readonly bool _optedOut;

    public GlobalPrivacyControlConsentFeature(IHttpContext context)
    {
        _optedOut = context.Request.Headers.TryGetValue(new HttpHeaderKey("Sec-GPC"), out HttpHeaderValue signal)
            && signal.Value == "1";
    }

    public string Name => nameof(GlobalPrivacyControlConsentFeature);

    public bool IsConsentNeeded => _optedOut;

    public bool HasConsent => false;

    public bool CanTrack => !_optedOut;

    public void GrantConsent()
    {
    }

    public void WithdrawConsent()
    {
    }
}
