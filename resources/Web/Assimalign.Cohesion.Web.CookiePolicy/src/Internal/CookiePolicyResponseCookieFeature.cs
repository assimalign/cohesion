using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Internal;

/// <summary>
/// The <see cref="IHttpResponseCookieFeature"/> the policy installs in place of the one it found, so
/// the <c>response.Cookies</c> extension property hands out the policy's collection.
/// </summary>
/// <remarks>
/// <para>
/// When the exchange already had a response cookie feature, its collection becomes the inner storage
/// and the cookies it already holds are judged at once. Otherwise the inner storage is created on the
/// first read of <see cref="Cookies"/>: the same header-synchronized <see cref="HttpCookieCollection"/>
/// the Http.Cookies default feature uses, so a response that never touches cookies pays for nothing but
/// this feature.
/// </para>
/// </remarks>
internal sealed class CookiePolicyResponseCookieFeature : IHttpResponseCookieFeature
{
    private readonly IHttpContext _context;
    private readonly CookiePolicyRules _rules;
    private readonly ICookieConsentFeature _consent;

    private CookiePolicyCookieCollection? _cookies;

    public CookiePolicyResponseCookieFeature(
        IHttpContext context,
        CookiePolicyRules rules,
        ICookieConsentFeature consent,
        IHttpCookieCollection? existing)
    {
        _context = context;
        _rules = rules;
        _consent = consent;

        if (existing is not null)
        {
            _cookies = new CookiePolicyCookieCollection(context, rules, consent, existing);
        }
    }

    /// <inheritdoc />
    public string Name => nameof(CookiePolicyResponseCookieFeature);

    /// <inheritdoc />
    public IHttpCookieCollection Cookies
    {
        get
        {
            if (_cookies is null)
            {
                _cookies = new CookiePolicyCookieCollection(
                    _context,
                    _rules,
                    _consent,
                    new HttpCookieCollection(_context.Response.Headers, HttpHeaderKey.SetCookie));

                // The new collection parsed any Set-Cookie field written before it existed; those cookies
                // are judged like the queue of a feature the policy replaced.
                _cookies.AdoptQueuedCookies();
            }

            return _cookies;
        }
    }

    /// <summary>
    /// Judges the cookies already queued in the collection of the feature this one replaced. Call it
    /// once, after both policy features are installed on the exchange.
    /// </summary>
    public void AdoptQueuedCookies() => _cookies?.AdoptQueuedCookies();
}
