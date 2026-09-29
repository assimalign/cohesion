using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents an immutable set of public JSON Web Keys (RFC 7517 §5) that one issuer signs with.
/// </summary>
/// <remarks>
/// A set is the unit an <see cref="IJsonWebTokenValidator"/> resolves per issuer: the token's
/// <c>kid</c> header selects the verification key with <see cref="Find(string?)"/>. A key without a
/// <c>kid</c> is retained but can never be selected, so every accepted token names its key.
/// </remarks>
public sealed class JsonWebKeySet
{
    /// <summary>Initializes a key set.</summary>
    /// <param name="keys">The keys, in lookup order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="keys"/> contains a <see langword="null"/> entry.</exception>
    public JsonWebKeySet(params IEnumerable<JsonWebKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var copy = new List<JsonWebKey>();
        foreach (JsonWebKey key in keys)
        {
            copy.Add(key ?? throw new ArgumentException("A JSON Web Key set cannot contain a null key.", nameof(keys)));
        }

        Keys = new ReadOnlyCollection<JsonWebKey>(copy);
    }

    /// <summary>Gets the keys in lookup order.</summary>
    public IReadOnlyList<JsonWebKey> Keys { get; }

    /// <summary>Finds the first key whose <c>kid</c> equals a key identifier.</summary>
    /// <param name="keyId">The key identifier, typically a token's <c>kid</c> header.</param>
    /// <returns>
    /// The first key whose <see cref="JsonWebKey.KeyId"/> ordinally equals <paramref name="keyId"/>, or
    /// <see langword="null"/> when <paramref name="keyId"/> is <see langword="null"/> or no key matches.
    /// </returns>
    public JsonWebKey? Find(string? keyId)
    {
        if (keyId is null)
        {
            return null;
        }

        for (int index = 0; index < Keys.Count; index++)
        {
            if (string.Equals(Keys[index].KeyId, keyId, StringComparison.Ordinal))
            {
                return Keys[index];
            }
        }

        return null;
    }
}
