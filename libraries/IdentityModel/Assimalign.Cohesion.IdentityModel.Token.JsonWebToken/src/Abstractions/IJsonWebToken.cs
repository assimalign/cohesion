using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents a JSON Web Token.
/// </summary>
public interface IJsonWebToken : IIdentityToken
{
    /// <summary>
    /// Gets the header values declared by the token.
    /// </summary>
    IReadOnlyDictionary<string, object?> Header { get; }

    /// <summary>
    /// Gets the declared signing algorithm.
    /// </summary>
    string? Algorithm { get; }

    /// <summary>
    /// Gets the compact token parts when raw token data is available.
    /// </summary>
    JsonWebTokenParts? Parts { get; }

    /// <summary>
    /// Attempts to read a header value.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">When this method returns, contains the header value, if one exists.</param>
    /// <returns><see langword="true" /> when the header exists; otherwise <see langword="false" />.</returns>
    bool TryGetHeaderValue(string name, out object? value);
}
