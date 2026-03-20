using System;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents the compact serialization segments of a JSON Web Token.
/// </summary>
public sealed class JsonWebTokenParts
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsonWebTokenParts" /> class.
    /// </summary>
    /// <param name="token">The compact token value.</param>
    /// <exception cref="ArgumentException">Thrown when the token does not contain exactly three segments.</exception>
    public JsonWebTokenParts(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var segments = token.Split('.');
        if (segments.Length != 3)
        {
            throw new ArgumentException(
                "A JSON Web Token must contain three dot-delimited segments.",
                nameof(token));
        }

        if (segments[0].Length == 0 || segments[1].Length == 0)
        {
            throw new ArgumentException(
                "A JSON Web Token header and payload segments must not be empty.",
                nameof(token));
        }

        Header = segments[0];
        Payload = segments[1];
        Signature = segments[2];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonWebTokenParts" /> class.
    /// </summary>
    /// <param name="header">The encoded header segment.</param>
    /// <param name="payload">The encoded payload segment.</param>
    /// <param name="signature">The encoded signature segment.</param>
    public JsonWebTokenParts(string header, string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentNullException.ThrowIfNull(signature);

        Header = header;
        Payload = payload;
        Signature = signature;
    }

    /// <summary>
    /// Gets the encoded header segment.
    /// </summary>
    public string Header { get; }

    /// <summary>
    /// Gets the encoded payload segment.
    /// </summary>
    public string Payload { get; }

    /// <summary>
    /// Gets the encoded signature segment.
    /// </summary>
    public string Signature { get; }

    /// <summary>
    /// Attempts to parse a compact JSON Web Token.
    /// </summary>
    /// <param name="token">The compact token value.</param>
    /// <param name="parts">When this method returns, contains the parsed parts, if parsing succeeded.</param>
    /// <returns><see langword="true" /> when parsing succeeded; otherwise <see langword="false" />.</returns>
    public static bool TryParse(string token, out JsonWebTokenParts? parts)
    {
        parts = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var segments = token.Split('.');
        if (segments.Length != 3 || segments[0].Length == 0 || segments[1].Length == 0)
        {
            return false;
        }

        parts = new JsonWebTokenParts(segments[0], segments[1], segments[2]);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat(Header, ".", Payload, ".", Signature);
}
