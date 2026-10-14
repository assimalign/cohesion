using System;
using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Internal;

/// <summary>
/// The response cookie collection the policy hands out: every <see cref="Add"/> is judged by
/// <see cref="CookiePolicyRules.TryApply"/> before the cookie reaches the inner collection, which owns
/// storage and the synchronization to <c>Set-Cookie</c>.
/// </summary>
/// <remarks>
/// <para>
/// The inner collection is the one the transports drain: the Http.Cookies default, bound to the
/// response headers, or whatever collection an earlier feature supplied. Enumeration, counts, and
/// removal delegate to it, so the collection always shows the cookies that will actually be sent.
/// </para>
/// <para>
/// A cookie the policy rewrites is queued as a copy. The collection remembers which copy stands for
/// which appended instance, so <see cref="Remove"/> and <see cref="Contains"/> still work when the
/// application passes the instance it appended. A dropped cookie is not queued: <see cref="Contains"/>
/// reports <see langword="false"/> and <see cref="Remove"/> finds nothing.
/// </para>
/// </remarks>
internal sealed class CookiePolicyCookieCollection : IHttpCookieCollection
{
    private readonly IHttpContext _context;
    private readonly CookiePolicyRules _rules;
    private readonly ICookieConsentFeature _consent;
    private readonly IHttpCookieCollection _inner;

    // Appended instance -> the rewritten copy queued for it. Only rewritten cookies have an entry.
    private Dictionary<HttpCookie, HttpCookie>? _rewritten;

    public CookiePolicyCookieCollection(
        IHttpContext context,
        CookiePolicyRules rules,
        ICookieConsentFeature consent,
        IHttpCookieCollection inner)
    {
        _context = context;
        _rules = rules;
        _consent = consent;
        _inner = inner;
    }

    /// <inheritdoc />
    public int Count => _inner.Count;

    /// <inheritdoc />
    public bool IsReadOnly => _inner.IsReadOnly;

    /// <summary>
    /// Judges <paramref name="item"/> and queues the cookie the policy issues for it, or drops it.
    /// </summary>
    /// <param name="item">The cookie the application appends.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    public void Add(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_rules.TryApply(_context, _consent, item, out HttpCookie? issued))
        {
            return;
        }

        if (!ReferenceEquals(issued, item))
        {
            // Reference identity: two distinct appends of equal-looking cookies are distinct entries.
            (_rewritten ??= new Dictionary<HttpCookie, HttpCookie>(ReferenceEqualityComparer.Instance))[item] = issued;
        }

        _inner.Add(issued);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _inner.Clear();
        _rewritten?.Clear();
    }

    /// <inheritdoc />
    public bool Contains(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_inner.Contains(item))
        {
            return true;
        }

        return _rewritten is not null
            && _rewritten.TryGetValue(item, out HttpCookie? issued)
            && _inner.Contains(issued);
    }

    /// <inheritdoc />
    public void CopyTo(HttpCookie[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public IEnumerator<HttpCookie> GetEnumerator() => _inner.GetEnumerator();

    /// <inheritdoc />
    public bool Remove(HttpCookie item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_rewritten is not null && _rewritten.Remove(item, out HttpCookie? issued))
        {
            return _inner.Remove(issued);
        }

        return _inner.Remove(item);
    }

    /// <summary>
    /// Judges the cookies the inner collection already holds, as if each were appended now, keeping their
    /// order. They were queued before the policy took the collection over: by middleware registered ahead
    /// of <c>UseCookiePolicy</c>, or parsed from a <c>Set-Cookie</c> field written before the collection
    /// existed.
    /// </summary>
    public void AdoptQueuedCookies()
    {
        if (_inner.Count == 0 || _inner.IsReadOnly)
        {
            return;
        }

        HttpCookie[] queued = new HttpCookie[_inner.Count];
        _inner.CopyTo(queued, 0);
        _inner.Clear();

        foreach (HttpCookie cookie in queued)
        {
            Add(cookie);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
