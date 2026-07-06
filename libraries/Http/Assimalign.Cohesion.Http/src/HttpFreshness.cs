using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Pure helpers for the RFC 9111 &#167; 4.2 freshness model: selecting a stored response's freshness
/// lifetime (&#167; 4.2.1), computing its current age (&#167; 4.2.3), and deciding whether it is still
/// fresh. All timestamps are supplied by the caller so the helpers are deterministic and
/// side-effect free — no ambient clock is read.
/// </summary>
/// <remarks>
/// These are the caching arithmetic primitives a response-cache (for example a future server-side
/// output cache) composes; the freshness <em>policy</em> — heuristic freshness (&#167; 4.2.2),
/// revalidation, and storage — belongs to that consumer, not to this protocol layer.
/// </remarks>
public static class HttpFreshness
{
    /// <summary>
    /// Selects the explicit freshness lifetime of a stored response per RFC 9111 &#167; 4.2.1: a
    /// shared cache prefers <c>s-maxage</c>, then either cache uses <c>max-age</c>, then falls back to
    /// <c>Expires</c> minus <c>Date</c>. Returns <see langword="null"/> when no explicit lifetime is
    /// present, signaling the caller to apply heuristic freshness (&#167; 4.2.2).
    /// </summary>
    /// <param name="cacheControl">The response's parsed <c>Cache-Control</c> directives.</param>
    /// <param name="expires">The response's <c>Expires</c> timestamp, or <see langword="null"/> when absent or unparseable.</param>
    /// <param name="date">The response's <c>Date</c> timestamp, or <see langword="null"/> when absent.</param>
    /// <param name="shared"><see langword="true"/> for a shared cache (which honors <c>s-maxage</c>); <see langword="false"/> for a private cache.</param>
    /// <returns>The explicit freshness lifetime, or <see langword="null"/> when none is determinable.</returns>
    public static TimeSpan? GetFreshnessLifetime(in HttpCacheControl cacheControl, DateTimeOffset? expires, DateTimeOffset? date, bool shared)
    {
        if (shared && cacheControl.SharedMaxAge is { } sharedMaxAge)
        {
            return sharedMaxAge;
        }
        if (cacheControl.MaxAge is { } maxAge)
        {
            return maxAge;
        }
        if (expires is { } expiresValue && date is { } dateValue)
        {
            TimeSpan lifetime = expiresValue - dateValue;
            return lifetime < TimeSpan.Zero ? TimeSpan.Zero : lifetime;
        }
        return null;
    }

    /// <summary>
    /// Computes the current age of a stored response per the RFC 9111 &#167; 4.2.3 algorithm, which
    /// combines the apparent age (from the <c>Date</c> header) with the age conveyed by the <c>Age</c>
    /// header, the request/response round-trip delay, and the time the response has since resided in
    /// cache.
    /// </summary>
    /// <param name="ageValue">The response's <c>Age</c> header value, or <see langword="null"/> when absent (treated as zero).</param>
    /// <param name="dateValue">The response's <c>Date</c> timestamp, or <see langword="null"/> when absent (apparent age is then zero).</param>
    /// <param name="requestTime">The local time the cache sent the request that produced the response.</param>
    /// <param name="responseTime">The local time the cache received the response.</param>
    /// <param name="now">The current time at which the age is being evaluated.</param>
    /// <returns>The response's current age (never negative).</returns>
    public static TimeSpan CalculateCurrentAge(TimeSpan? ageValue, DateTimeOffset? dateValue, DateTimeOffset requestTime, DateTimeOffset responseTime, DateTimeOffset now)
    {
        TimeSpan apparentAge = dateValue is { } date
            ? Max(TimeSpan.Zero, responseTime - date)
            : TimeSpan.Zero;

        TimeSpan responseDelay = responseTime - requestTime;
        TimeSpan correctedAgeValue = (ageValue ?? TimeSpan.Zero) + responseDelay;
        TimeSpan correctedInitialAge = Max(apparentAge, correctedAgeValue);
        TimeSpan residentTime = now - responseTime;
        TimeSpan currentAge = correctedInitialAge + residentTime;

        return currentAge < TimeSpan.Zero ? TimeSpan.Zero : currentAge;
    }

    /// <summary>
    /// Determines whether a stored response is still fresh (RFC 9111 &#167; 4.2): fresh while its
    /// freshness lifetime is strictly greater than its current age.
    /// </summary>
    /// <param name="freshnessLifetime">The freshness lifetime from <see cref="GetFreshnessLifetime"/> (or a heuristic).</param>
    /// <param name="currentAge">The current age from <see cref="CalculateCurrentAge"/>.</param>
    /// <returns><see langword="true"/> when the response is fresh.</returns>
    public static bool IsFresh(TimeSpan freshnessLifetime, TimeSpan currentAge)
        => freshnessLifetime > currentAge;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}
