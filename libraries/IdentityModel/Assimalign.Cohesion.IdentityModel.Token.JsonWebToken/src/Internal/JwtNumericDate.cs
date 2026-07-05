using System;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Converts RFC 7519 NumericDate claim values into <see cref="DateTimeOffset" /> without ever
/// throwing on an out-of-range or non-integral wire value — an extreme <c>exp</c> such as
/// <c>1e300</c> degrades to <see langword="null" /> (leaving the raw claim intact for a
/// validator to diagnose) rather than crashing the parse of an otherwise structurally valid
/// token.
/// </summary>
internal static class JwtNumericDate
{
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>
    /// Converts a whole-second Unix timestamp, or <see langword="null" /> when it is outside the
    /// representable <see cref="DateTimeOffset" /> range.
    /// </summary>
    public static DateTimeOffset? ToDateTimeOffset(long unixSeconds)
    {
        if (unixSeconds < MinUnixSeconds || unixSeconds > MaxUnixSeconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    /// <summary>
    /// Projects a NumericDate claim value onto a <see cref="DateTimeOffset" />: an integral
    /// value maps directly; a fractional value truncates to whole seconds; anything else (a
    /// non-numeric value, NaN, infinity, or an out-of-range magnitude) yields
    /// <see langword="null" />.
    /// </summary>
    public static DateTimeOffset? FromClaimValue(IdentityClaimValue value)
    {
        if (value.TryGetInteger(out var seconds))
        {
            return ToDateTimeOffset(seconds);
        }

        if (value.TryGetDouble(out var fractional))
        {
            if (double.IsNaN(fractional) || double.IsInfinity(fractional) ||
                fractional < MinUnixSeconds || fractional > MaxUnixSeconds)
            {
                return null;
            }

            return ToDateTimeOffset((long)Math.Floor(fractional));
        }

        return null;
    }
}
