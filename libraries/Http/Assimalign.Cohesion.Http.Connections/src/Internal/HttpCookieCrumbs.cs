using System.Collections.Generic;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Collects the crumbs of a split <c>Cookie</c> field while an HTTP/2 or HTTP/3 field section is folded,
/// and joins them with <c>"; "</c> once the section ends (RFC 9113 §8.2.3, RFC 9114 §4.2.1).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpFieldNormalization.CombineFieldValue"/> joins one crumb onto the value so far, and a
/// string cannot grow in place, so it copies the whole value for every crumb: <c>n</c> crumbs cost time
/// and allocation quadratic in <c>n</c>. A section holds as many crumbs as its decoded-size limit lets
/// through, and a host may raise that limit, so both versions collect the crumbs here and join them once,
/// in time linear in their total length (#1082). Every other repeated field still combines through
/// <see cref="HttpFieldNormalization.CombineFieldValue"/>, which appends in amortized constant time.
/// </para>
/// <para>
/// A mutable struct: keep it in one local or field and pass it by reference, never by copy.
/// </para>
/// </remarks>
internal struct HttpCookieCrumbs
{
    // The crumbs in wire order, the first one included; null until a Cookie field repeats.
    private List<string>? _crumbs;

    /// <summary>
    /// Collects a repeated field line when it is a <c>Cookie</c> crumb.
    /// </summary>
    /// <param name="key">The field name.</param>
    /// <param name="existing">The value the section already holds for the field: its first crumb.</param>
    /// <param name="crumb">The repeated field line's value.</param>
    /// <returns>
    /// <see langword="true"/> when the field is <c>Cookie</c> and the crumb was collected;
    /// <see langword="false"/> for any other field, which the caller combines.
    /// </returns>
    public bool TryAdd(HttpHeaderKey key, HttpHeaderValue existing, string crumb)
    {
        if (key != HttpHeaderKey.Cookie)
        {
            return false;
        }

        // The section keeps the first crumb until Join replaces it, so existing is the first crumb on every
        // repeat; it is read only once, when the list starts.
        (_crumbs ??= [existing.Value]).Add(crumb);
        return true;
    }

    /// <summary>
    /// Replaces the section's <c>Cookie</c> field with its crumbs joined by <c>"; "</c>. The field keeps
    /// the position its first crumb arrived in. Does nothing when no <c>Cookie</c> field repeated.
    /// </summary>
    /// <param name="fields">The folded field section.</param>
    public readonly void Join(HttpHeaderCollection fields)
    {
        if (_crumbs is not null)
        {
            fields[HttpHeaderKey.Cookie] = string.Join("; ", _crumbs);
        }
    }
}
