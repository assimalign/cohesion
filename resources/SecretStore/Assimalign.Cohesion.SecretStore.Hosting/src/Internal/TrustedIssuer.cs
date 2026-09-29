using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

internal sealed class TrustedIssuer
{
    internal TrustedIssuer(
        string owner,
        string issuer,
        JsonElement publicKey,
        IReadOnlyList<string>? allowedCommandKinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (!JsonWebKey.TryParse(publicKey, out JsonWebKey? key))
        {
            throw new ArgumentException("A trusted issuer key must be a JSON object.", nameof(publicKey));
        }

        if (!key.TryValidateEs256SigningKey(out string? failure))
        {
            throw new ArgumentException(failure, nameof(publicKey));
        }

        Owner = owner;
        Issuer = issuer;
        PublicKey = publicKey.Clone();
        Keys = new JsonWebKeySet(key);
        AllowedCommandKinds = Array.AsReadOnly((allowedCommandKinds ?? []).Select(static kind =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            return kind;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal string Owner { get; }

    internal string Issuer { get; }

    internal JsonElement PublicKey { get; }

    internal JsonWebKeySet Keys { get; }

    internal IReadOnlyList<string> AllowedCommandKinds { get; }

    internal static IReadOnlyList<string> ReadAllowedCommandKinds(JsonElement entry)
    {
        if (!entry.TryGetProperty("allowedCommandKinds", out JsonElement kinds)) { return []; }
        if (kinds.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("allowedCommandKinds must be a string array.");
        }
        return kinds.EnumerateArray().Select(static value =>
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()! : throw new ArgumentException("allowedCommandKinds entries must be nonblank strings.")).ToArray();
    }
}
