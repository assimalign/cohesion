using System;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

internal sealed class ConfigurationTrustedIssuer
{
    internal ConfigurationTrustedIssuer(string issuer, JsonElement publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (!JsonWebKey.TryParse(publicKey, out JsonWebKey? key))
        {
            throw new ArgumentException("A trusted issuer key must be a JSON object.", nameof(publicKey));
        }

        if (!key.TryValidateEs256SigningKey(out string? failure))
        {
            throw new ArgumentException(failure, nameof(publicKey));
        }

        Issuer = issuer;
        PublicKey = publicKey.Clone();
        KeyId = key.KeyId!;
        Keys = new JsonWebKeySet(key);
    }

    internal string Issuer { get; }

    internal string KeyId { get; }

    internal JsonElement PublicKey { get; }

    internal JsonWebKeySet Keys { get; }
}
