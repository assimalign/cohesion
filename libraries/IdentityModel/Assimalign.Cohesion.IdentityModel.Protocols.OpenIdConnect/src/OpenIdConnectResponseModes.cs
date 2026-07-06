using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the <c>response_mode</c> values and their mapping onto the family's
/// transport-shaped <see cref="ProtocolBinding" /> vocabulary. The wire spellings live
/// here (the owning branch); the binding vocabulary deliberately has one name per wire
/// shape, so <c>query</c> maps to <see cref="ProtocolBinding.HttpRedirect" /> rather than
/// having its own binding value.
/// </summary>
public static class OpenIdConnectResponseModes
{
    /// <summary>
    /// Response parameters in the redirect query component (<c>query</c>).
    /// </summary>
    public const string Query = "query";

    /// <summary>
    /// Response parameters in the redirect fragment component (<c>fragment</c>).
    /// </summary>
    public const string Fragment = "fragment";

    /// <summary>
    /// Response parameters as an auto-submitted HTML form POST (<c>form_post</c>).
    /// </summary>
    public const string FormPost = "form_post";

    /// <summary>
    /// Maps a <c>response_mode</c> wire value onto the family's transport-shaped binding
    /// vocabulary.
    /// </summary>
    /// <param name="responseMode">The response mode wire value.</param>
    /// <returns>
    /// The matching binding, or <see cref="ProtocolBinding.Unknown" /> when the response
    /// mode is not recognized.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="responseMode" /> is null or whitespace.</exception>
    public static ProtocolBinding GetBinding(string responseMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseMode);

        return responseMode switch
        {
            Query => ProtocolBinding.HttpRedirect,
            Fragment => ProtocolBinding.HttpFragment,
            FormPost => ProtocolBinding.HttpPost,
            _ => ProtocolBinding.Unknown,
        };
    }
}
