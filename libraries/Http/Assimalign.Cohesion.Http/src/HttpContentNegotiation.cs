using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Server-driven content negotiation (RFC 9110 &#167; 12.1, &#167; 12.5): selects the best
/// server representation for a request's <c>Accept</c> family headers. Given the client's
/// weighted preferences and the representations a server can produce (listed in the server's own
/// preference order), each selector returns the winning candidate or reports that nothing is
/// acceptable &#8212; the signal a caller turns into a <c>406 Not Acceptable</c> response.
/// </summary>
/// <remarks>
/// <para>
/// Selection follows RFC 9110 &#167; 12.5.1: for each server representation the quality is taken
/// from the <em>most specific</em> matching client range; the representation with the highest
/// resulting quality wins, and ties are broken in favor of the server's stated preference order.
/// A quality of <c>0</c> means "not acceptable". When the client sends no preference header, every
/// representation is acceptable and the server's first (most preferred) option is chosen.
/// </para>
/// </remarks>
public static class HttpContentNegotiation
{
    private const string Identity = "identity";

    /// <summary>
    /// Selects the best media type from an already-parsed <c>Accept</c> list.
    /// </summary>
    /// <param name="accept">The client's weighted media ranges (see <see cref="HttpAcceptParser.ParseAccept"/>).</param>
    /// <param name="serverOptions">The media types the server can produce, in server preference order.</param>
    /// <param name="selected">When this method returns <see langword="true"/>, the negotiated media type.</param>
    /// <returns><see langword="true"/> when an acceptable representation exists; otherwise <see langword="false"/> (a 406 signal).</returns>
    public static bool TryNegotiateMediaType(
        IReadOnlyList<HttpMediaTypeQuality> accept,
        IReadOnlyList<HttpMediaType> serverOptions,
        out HttpMediaType selected)
    {
        selected = default;
        if (serverOptions is null || serverOptions.Count == 0)
        {
            return false;
        }

        // RFC 9110 §12.5.1: a missing Accept means the client accepts all media types.
        if (accept is null || accept.Count == 0)
        {
            selected = serverOptions[0];
            return true;
        }

        int bestQuality = 0;
        bool found = false;
        for (int i = 0; i < serverOptions.Count; i++)
        {
            HttpMediaType option = serverOptions[i];
            int optionQuality = QualityForMediaType(accept, option);
            if (optionQuality > bestQuality)
            {
                bestQuality = optionQuality;
                selected = option;
                found = true;
            }
        }

        if (!found)
        {
            selected = default;
        }
        return found;
    }

    /// <summary>
    /// Parses <paramref name="acceptHeader"/> and selects the best media type for the server's
    /// representations.
    /// </summary>
    /// <param name="acceptHeader">The raw <c>Accept</c> header value, or <see langword="null"/>.</param>
    /// <param name="serverOptions">The media types the server can produce, in server preference order.</param>
    /// <param name="selected">When this method returns <see langword="true"/>, the negotiated media type.</param>
    /// <returns><see langword="true"/> when an acceptable representation exists; otherwise <see langword="false"/>.</returns>
    public static bool TryNegotiateMediaType(
        string? acceptHeader,
        IReadOnlyList<HttpMediaType> serverOptions,
        out HttpMediaType selected)
        => TryNegotiateMediaType(HttpAcceptParser.ParseAccept(acceptHeader), serverOptions, out selected);

    /// <summary>
    /// Selects the best value token from a weighted preference list (suitable for
    /// <c>Accept-Charset</c> or <c>Accept-Language</c>). Exact matches take precedence over the
    /// <c>*</c> wildcard; a value that is neither listed nor covered by <c>*</c> is not acceptable.
    /// </summary>
    /// <param name="accepted">The client's weighted values (see the <c>HttpAcceptParser.Parse*</c> methods).</param>
    /// <param name="serverOptions">The values the server can produce, in server preference order.</param>
    /// <param name="selected">When this method returns <see langword="true"/>, the negotiated value.</param>
    /// <returns><see langword="true"/> when an acceptable value exists; otherwise <see langword="false"/>.</returns>
    public static bool TrySelectByQuality(
        IReadOnlyList<HttpQualityValue> accepted,
        IReadOnlyList<string> serverOptions,
        out string selected)
    {
        selected = string.Empty;
        if (serverOptions is null || serverOptions.Count == 0)
        {
            return false;
        }

        if (accepted is null || accepted.Count == 0)
        {
            selected = serverOptions[0];
            return true;
        }

        int bestQuality = 0;
        bool found = false;
        foreach (string option in serverOptions)
        {
            int quality = QualityForToken(accepted, option);
            if (quality > bestQuality)
            {
                bestQuality = quality;
                selected = option;
                found = true;
            }
        }

        if (!found)
        {
            selected = string.Empty;
        }
        return found;
    }

    /// <summary>
    /// Selects a content-coding for a response given an <c>Accept-Encoding</c> preference list
    /// (RFC 9110 &#167; 12.5.3). The <c>identity</c> coding (send the response uncompressed) is
    /// acceptable by default and is chosen unless a listed coding is preferred; it is excluded only
    /// when the client sends <c>identity;q=0</c> or a <c>*;q=0</c> with no overriding identity entry.
    /// Ties between a codable representation and identity are resolved in favor of the server coding.
    /// </summary>
    /// <param name="accepted">The client's weighted content-codings (see <see cref="HttpAcceptParser.ParseAcceptEncoding"/>).</param>
    /// <param name="serverCodings">The codings the server can apply, in server preference order (must not include <c>identity</c>).</param>
    /// <param name="selected">
    /// When this method returns <see langword="true"/>, the chosen coding; the literal
    /// <c>identity</c> indicates the response should be sent without a content-coding.
    /// </param>
    /// <returns><see langword="true"/> when an acceptable coding exists; otherwise <see langword="false"/> (a 406 signal).</returns>
    public static bool TrySelectEncoding(
        IReadOnlyList<HttpQualityValue> accepted,
        IReadOnlyList<string> serverCodings,
        out string selected)
    {
        int bestQuality = 0;
        string? bestCoding = null;
        if (serverCodings is not null)
        {
            foreach (string coding in serverCodings)
            {
                int quality = QualityForToken(accepted, coding);
                if (quality > bestQuality)
                {
                    bestQuality = quality;
                    bestCoding = coding;
                }
            }
        }

        // RFC 9110 §12.5.3: identity is acceptable by default; it is refused only by an explicit
        // identity;q=0 or a *;q=0 with no overriding identity entry.
        bool identityExplicit = TryResolveIdentityQuality(accepted, out int identityQuality);
        bool identityAcceptable = identityExplicit ? identityQuality > 0 : true;

        if (bestCoding is not null && bestQuality > 0)
        {
            // Compress with the best acceptable coding, unless the client explicitly ranks
            // identity (no transformation) strictly higher than that coding.
            if (identityExplicit && identityQuality > bestQuality)
            {
                selected = Identity;
                return true;
            }
            selected = bestCoding;
            return true;
        }

        // No server coding is acceptable: send the response uncompressed if identity is allowed.
        if (identityAcceptable)
        {
            selected = Identity;
            return true;
        }

        selected = string.Empty;
        return false;
    }

    /// <summary>
    /// Parses <paramref name="acceptEncodingHeader"/> and selects a content-coding for the response.
    /// </summary>
    /// <param name="acceptEncodingHeader">The raw <c>Accept-Encoding</c> header value, or <see langword="null"/>.</param>
    /// <param name="serverCodings">The codings the server can apply, in server preference order (must not include <c>identity</c>).</param>
    /// <param name="selected">When this method returns <see langword="true"/>, the chosen coding (<c>identity</c> means uncompressed).</param>
    /// <returns><see langword="true"/> when an acceptable coding exists; otherwise <see langword="false"/>.</returns>
    public static bool TrySelectEncoding(
        string? acceptEncodingHeader,
        IReadOnlyList<string> serverCodings,
        out string selected)
        => TrySelectEncoding(HttpAcceptParser.ParseAcceptEncoding(acceptEncodingHeader), serverCodings, out selected);

    private static int QualityForMediaType(IReadOnlyList<HttpMediaTypeQuality> accept, HttpMediaType option)
    {
        // RFC 9110 §12.5.1: the most specific matching range determines the quality.
        int quality = 0;
        int specificity = -1;
        foreach (HttpMediaTypeQuality entry in accept)
        {
            if (!entry.MediaType.Includes(option))
            {
                continue;
            }
            int entrySpecificity = entry.MediaType.Specificity;
            if (entrySpecificity > specificity
                || (entrySpecificity == specificity && entry.Quality.PerMille > quality))
            {
                specificity = entrySpecificity;
                quality = entry.Quality.PerMille;
            }
        }
        return quality;
    }

    private static int QualityForToken(IReadOnlyList<HttpQualityValue> accepted, string option)
    {
        int wildcard = -1;
        foreach (HttpQualityValue entry in accepted)
        {
            if (entry.Matches(option))
            {
                // An exact match always wins over the wildcard.
                return entry.Quality.PerMille;
            }
            if (entry.IsWildcard && wildcard < 0)
            {
                wildcard = entry.Quality.PerMille;
            }
        }
        return wildcard < 0 ? 0 : wildcard;
    }

    private static bool TryResolveIdentityQuality(IReadOnlyList<HttpQualityValue> accepted, out int quality)
    {
        quality = 0;
        if (accepted is null || accepted.Count == 0)
        {
            // No preference expressed: identity is acceptable by default with no explicit ranking.
            return false;
        }

        int wildcard = -1;
        foreach (HttpQualityValue entry in accepted)
        {
            if (entry.Matches(Identity))
            {
                // An explicit identity entry is authoritative, even identity;q=0.
                quality = entry.Quality.PerMille;
                return true;
            }
            if (entry.IsWildcard && wildcard < 0)
            {
                wildcard = entry.Quality.PerMille;
            }
        }

        if (wildcard >= 0)
        {
            // '*' governs identity in the absence of an explicit identity entry.
            quality = wildcard;
            return true;
        }

        // Identity is not mentioned and there is no wildcard: acceptable by default, unranked.
        return false;
    }
}
