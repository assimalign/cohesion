using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents an immutable JSON Web Token.
/// </summary>
public sealed class JsonWebToken : IdentityToken, IJsonWebToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsonWebToken" /> class.
    /// </summary>
    /// <param name="descriptor">The JWT descriptor.</param>
    public JsonWebToken(JsonWebTokenDescriptor descriptor)
        : base(IdentityTokenKind.JsonWebToken, NormalizeDescriptor(descriptor))
    {
        Header = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(descriptor.Header, StringComparer.Ordinal));
        Parts = descriptor.Parts ?? ParseParts(descriptor.RawData);
        Algorithm = descriptor.Algorithm ?? GetStringHeaderValue(Header, "alg");
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Header { get; }

    /// <inheritdoc />
    public string? Algorithm { get; }

    /// <inheritdoc />
    public JsonWebTokenParts? Parts { get; }

    /// <inheritdoc />
    public bool TryGetHeaderValue(string name, out object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Header.TryGetValue(name, out value);
    }

    private static JsonWebTokenDescriptor NormalizeDescriptor(JsonWebTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Parts is not null && string.IsNullOrEmpty(descriptor.RawData))
        {
            descriptor.RawData = descriptor.Parts.ToString();
        }

        if (string.IsNullOrWhiteSpace(descriptor.TokenType))
        {
            descriptor.TokenType = GetStringHeaderValue(descriptor.Header, "typ");
        }

        return descriptor;
    }

    private static JsonWebTokenParts? ParseParts(string? rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            return null;
        }

        return new JsonWebTokenParts(rawData);
    }

    private static string? GetStringHeaderValue(
        IEnumerable<KeyValuePair<string, object?>> header,
        string name)
    {
        foreach (var item in header)
        {
            if (!string.Equals(item.Key, name, StringComparison.Ordinal))
            {
                continue;
            }

            return item.Value as string;
        }

        return null;
    }
}
