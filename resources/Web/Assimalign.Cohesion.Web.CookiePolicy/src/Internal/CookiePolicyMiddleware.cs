using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Internal;

/// <summary>
/// Installs the policy on an exchange and passes it on. Enforcement happens later, inside the replaced
/// response cookie collection, each time a cookie is appended.
/// </summary>
/// <remarks>
/// <para>
/// The feature collection is keyed by <see cref="IHttpFeature.Name"/> and type lookups return the first
/// match, so installing a second <see cref="IHttpResponseCookieFeature"/> beside the existing one would
/// leave the existing one in charge. The middleware removes the existing feature's slot first and
/// adopts its collection as inner storage, so a cookie feature installed earlier (the Http.Cookies
/// default, or a richer one such as a signing feature) keeps working underneath the policy.
/// </para>
/// <para>
/// The installed features stay on the exchange after <c>next</c> returns, so cookies that middleware
/// registered ahead of the policy appends on the way out are judged too, provided it reads
/// <c>response.Cookies</c> when it appends rather than holding a collection it read before.
/// </para>
/// </remarks>
internal sealed class CookiePolicyMiddleware : IWebApplicationMiddleware
{
    private readonly CookiePolicyRules _rules;

    public CookiePolicyMiddleware(CookiePolicyRules rules)
    {
        _rules = rules;
    }

    /// <inheritdoc />
    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IHttpFeatureCollection features = context.Features;

        // Consent is decided once per exchange, by the first consent feature installed on it. A nested
        // registration (a branch that registers its own policy) reuses the outer registration's, while its
        // attribute rules add to the outer ones; an application-supplied feature installed earlier, for
        // example one driven by a consent-management platform, is honored the same way.
        ICookieConsentFeature? consent = features.Get<ICookieConsentFeature>();
        if (consent is null)
        {
            consent = new CookieConsentFeature(context, _rules);
            features.Set<ICookieConsentFeature>(consent);
        }

        IHttpResponseCookieFeature? existing = features.Get<IHttpResponseCookieFeature>();
        CookiePolicyResponseCookieFeature feature = new(context, _rules, consent, existing?.Cookies);

        if (existing is not null)
        {
            features.Remove(existing.Name);
        }

        features.Set<IHttpResponseCookieFeature>(feature);
        feature.AdoptQueuedCookies();

        return next.Invoke(context);
    }
}
