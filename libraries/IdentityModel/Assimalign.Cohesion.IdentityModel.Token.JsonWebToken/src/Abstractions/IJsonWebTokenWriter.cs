using System;
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Writes JSON Web Tokens as signed compact JWS values.
/// </summary>
public interface IJsonWebTokenWriter
{
    /// <summary>
    /// Writes and signs a compact JSON Web Token from the existing token descriptor model.
    /// The descriptor's issuer, audiences, expiration, and identifier members project to the
    /// <c>iss</c>, <c>aud</c>, <c>exp</c>, and <c>jti</c> claims, respectively; remaining
    /// claims come from <see cref="IdentityTokenDescriptor.Claims" />.
    /// </summary>
    /// <param name="descriptor">The token contents to write.</param>
    /// <returns>The signed compact JWS value.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="descriptor" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the descriptor contains an invalid value or supplies the same registered
    /// claim through both a typed member and the claim collection.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the descriptor requests an unencoded JWS payload.
    /// </exception>
    /// <exception cref="CryptographicException">Thrown when the configured key cannot sign the token.</exception>
    string Write(JsonWebTokenDescriptor descriptor);
}
