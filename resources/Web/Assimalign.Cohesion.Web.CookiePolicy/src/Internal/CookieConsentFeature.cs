using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Internal;

/// <summary>
/// The default <see cref="ICookieConsentFeature"/>: consent state for one exchange, read lazily and
/// cached, plus the grant and withdraw verbs that append the consent cookie through the policy.
/// </summary>
/// <remarks>
/// Both answers are computed on first use and cached for the exchange: the consent predicate runs at
/// most once, and the request's cookies are parsed only when a non-essential cookie is appended while
/// consent is needed or the application asks. The policy installs this feature only when the exchange
/// has no <see cref="ICookieConsentFeature"/> yet, so consent is decided once per exchange: a nested
/// <c>UseCookiePolicy</c> registration reuses the outer registration's instance, and a consent feature
/// the application installed earlier takes this one's place.
/// </remarks>
internal sealed class CookieConsentFeature : ICookieConsentFeature
{
    private readonly IHttpContext _context;
    private readonly CookiePolicyRules _rules;

    private bool? _isConsentNeeded;
    private bool? _hasConsent;

    public CookieConsentFeature(IHttpContext context, CookiePolicyRules rules)
    {
        _context = context;
        _rules = rules;
    }

    /// <inheritdoc />
    public string Name => nameof(CookieConsentFeature);

    /// <inheritdoc />
    public bool IsConsentNeeded => _isConsentNeeded ??= _rules.IsConsentNeeded(_context);

    /// <inheritdoc />
    public bool HasConsent => _hasConsent ??= _rules.HasConsentCookie(_context);

    /// <inheritdoc />
    public bool CanTrack => !IsConsentNeeded || HasConsent;

    /// <inheritdoc />
    public void GrantConsent()
    {
        if (!HasConsent)
        {
            IssueConsentCookie(_rules.CreateConsentCookie());
        }

        _hasConsent = true;
    }

    /// <inheritdoc />
    public void WithdrawConsent()
    {
        if (HasConsent)
        {
            IssueConsentCookie(_rules.CreateConsentDeletionCookie());
        }

        _hasConsent = false;
    }

    /// <summary>
    /// Appends the consent cookie, or its deletion, through the response cookie collection, so the
    /// policy's floors apply to it like any other cookie. A consent cookie already queued in this
    /// exchange is replaced, so a grant followed by a withdrawal emits exactly one line for the name.
    /// </summary>
    private void IssueConsentCookie(HttpCookie cookie)
    {
        // A committed head can carry no new Set-Cookie. Skip rather than fault: the state change still
        // holds for the rest of the exchange.
        if (_context.Response.Headers.IsReadOnly)
        {
            return;
        }

        IHttpCookieCollection cookies = _context.Response.Cookies;

        List<HttpCookie>? stale = null;
        foreach (HttpCookie queued in cookies)
        {
            if (string.Equals(queued.Name, cookie.Name, StringComparison.Ordinal))
            {
                (stale ??= []).Add(queued);
            }
        }

        if (stale is not null)
        {
            foreach (HttpCookie queued in stale)
            {
                cookies.Remove(queued);
            }
        }

        cookies.Add(cookie);
    }
}
