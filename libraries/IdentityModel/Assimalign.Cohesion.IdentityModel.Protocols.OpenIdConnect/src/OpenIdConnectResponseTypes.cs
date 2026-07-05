using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the atomic <c>response_type</c> values and the order-insensitive comparison the
/// OAuth 2.0 Multiple Response Type Encoding Practices specification mandates.
/// </summary>
/// <remarks>
/// A composite response type such as <c>code id_token</c> is a space-delimited,
/// order-insensitive set: <c>id_token code</c> names the same response type. Never compare
/// response types with ordinal string equality — use <see cref="Matches" />. Only atomic
/// values ship as constants so no canonical ordering is baked into consumer code.
/// </remarks>
public static class OpenIdConnectResponseTypes
{
    /// <summary>
    /// The authorization code response type (<c>code</c>).
    /// </summary>
    public const string Code = "code";

    /// <summary>
    /// The ID token response type (<c>id_token</c>).
    /// </summary>
    public const string IdToken = "id_token";

    /// <summary>
    /// The access token response type (<c>token</c>).
    /// </summary>
    public const string Token = "token";

    /// <summary>
    /// The no-artifact response type (<c>none</c>): a successful authorization response
    /// carries no code, token, or ID token.
    /// </summary>
    public const string None = "none";

    /// <summary>
    /// Determines whether two <c>response_type</c> values name the same response type,
    /// comparing their space-delimited parts as unordered sets. Duplicate parts collapse
    /// (a malformed <c>"code code"</c> names the same set as <c>"code"</c>) and the
    /// relation is symmetric.
    /// </summary>
    /// <param name="left">The first response type value.</param>
    /// <param name="right">The second response type value.</param>
    /// <returns><see langword="true" /> when the values name the same response type; otherwise <see langword="false" />.</returns>
    public static bool Matches(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var leftParts = new HashSet<string>(Split(left), StringComparer.Ordinal);
        var rightParts = new HashSet<string>(Split(right), StringComparer.Ordinal);

        return leftParts.SetEquals(rightParts);
    }

    /// <summary>
    /// Splits a <c>response_type</c> value into its atomic parts.
    /// </summary>
    /// <param name="responseType">The response type value.</param>
    /// <returns>The atomic parts, in wire order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="responseType" /> is null.</exception>
    public static string[] Split(string responseType)
    {
        ArgumentNullException.ThrowIfNull(responseType);
        return responseType.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
