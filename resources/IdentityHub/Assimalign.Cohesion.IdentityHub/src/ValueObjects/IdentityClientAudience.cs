using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// An audience: the identifier of an API that an app registration exposes, as a token names it
/// in its <c>aud</c> claim and a request names it as the resource it wants a token for.
/// </summary>
/// <remarks>
/// <para>
/// An audience is the StringOrURI of RFC 7519 section 2, narrowed to 1 to 256 visible ASCII
/// characters: any such string, and when it holds a <c>:</c> it starts with a URI scheme and
/// carries no fragment (<c>api://orders</c>, <c>https://orders.example</c>,
/// <c>urn:example:orders</c>, <c>orders</c>).
/// </para>
/// <para>
/// Audiences compare ordinally and are never normalized: <c>https://orders.example</c> and
/// <c>https://orders.example/</c> are two audiences. An audience belongs to one registration
/// at a time, so that a token's audience always names one API. An audience is not a secret.
/// <see langword="default"/> holds no audience and every registration boundary refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityClientAudience
{
    /// <summary>
    /// The longest audience, in characters.
    /// </summary>
    public const int MaxLength = IdentityClientGrammar.MaxAudienceLength;

    /// <summary>
    /// Determines whether a value is an audience.
    /// </summary>
    /// <param name="value">The candidate audience.</param>
    /// <param name="message">The reason the audience is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the audience grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityClientGrammar.IsAudience(value))
        {
            message = ErrorMessages.AudienceInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
