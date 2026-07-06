using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Parses a compact JWS-serialized JSON Web Token into a <see cref="JsonWebTokenDescriptor" />
/// using only reflection-free <see cref="System.Text.Json" /> readers, so the path is
/// NativeAOT- and trimming-safe. It reads the document faithfully — it never verifies a
/// signature — and rejects the RFC 8725 §2.3 duplicate-member ambiguity rather than silently
/// resolving it.
/// </summary>
internal static class JsonWebTokenParser
{
    public static JsonWebTokenDescriptor Parse(string compact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compact);

        if (!JsonWebTokenParts.TryParse(compact, out var parts) || parts is null)
        {
            throw new FormatException(
                "A JSON Web Token must contain three dot-delimited segments with non-empty header and payload segments.");
        }

        var descriptor = new JsonWebTokenDescriptor
        {
            Parts = parts,
            RawData = compact,
        };

        ReadHeader(DecodeSegment(parts.Header, "header"), descriptor.Header);
        ReadPayload(DecodeSegment(parts.Payload, "payload"), descriptor);

        if (descriptor.Header.Parameters.TryGetValue(JoseHeaderParameterNames.Type, out var typ) &&
            typ.TryGetString(out var tokenType))
        {
            descriptor.TokenType = tokenType;
        }

        return descriptor;
    }

    private static void ReadHeader(byte[] json, JoseHeaderDescriptor header)
    {
        using var document = ParseJsonObject(json, "header");
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw DuplicateMember(property.Name, "header");
            }

            header.Parameters[property.Name] = Convert(property.Value);
        }
    }

    private static void ReadPayload(byte[] json, JsonWebTokenDescriptor descriptor)
    {
        using var document = ParseJsonObject(json, "payload");
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? issuer = null;
        string? subject = null;
        string? tokenId = null;
        IdentityClaimValue? expiration = null;
        IdentityClaimValue? notBefore = null;
        IdentityClaimValue? issuedAt = null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var name = property.Name;
            if (!seen.Add(name))
            {
                throw DuplicateMember(name, "payload");
            }

            // aud is string-or-array (RFC 7519 §4.1.3): fold both shapes into the neutral
            // audience list AND emit one claim per audience, so the two surfaces agree.
            if (string.Equals(name, IdentityClaimTypes.Audience, StringComparison.Ordinal))
            {
                ReadAudiences(property.Value, descriptor);
                continue;
            }

            var value = Convert(property.Value);
            descriptor.Claims.Add(new IdentityClaim(name, value));

            if (string.Equals(name, IdentityClaimTypes.Issuer, StringComparison.Ordinal) && value.TryGetString(out var iss))
            {
                issuer = iss;
            }
            else if (string.Equals(name, IdentityClaimTypes.Subject, StringComparison.Ordinal) && value.TryGetString(out var sub))
            {
                subject = sub;
            }
            else if (string.Equals(name, IdentityClaimTypes.JwtId, StringComparison.Ordinal) && value.TryGetString(out var jti))
            {
                tokenId = jti;
            }
            else if (string.Equals(name, IdentityClaimTypes.ExpirationTime, StringComparison.Ordinal))
            {
                expiration = value;
            }
            else if (string.Equals(name, IdentityClaimTypes.NotBefore, StringComparison.Ordinal))
            {
                notBefore = value;
            }
            else if (string.Equals(name, IdentityClaimTypes.IssuedAt, StringComparison.Ordinal))
            {
                issuedAt = value;
            }
        }

        descriptor.Issuer = issuer;
        descriptor.Id = tokenId;
        if (subject is not null)
        {
            descriptor.Subject = new SubjectIdentifier(subject, issuer: issuer);
        }

        // The base temporal members are bounded projections; the raw NumericDate stays in Claims.
        if (expiration is { } exp)
        {
            descriptor.ExpiresAt = JwtNumericDate.FromClaimValue(exp);
        }

        if (notBefore is { } nbf)
        {
            descriptor.NotBefore = JwtNumericDate.FromClaimValue(nbf);
        }

        if (issuedAt is { } iat)
        {
            descriptor.IssuedAt = JwtNumericDate.FromClaimValue(iat);
        }
    }

    private static void ReadAudiences(JsonElement audience, JsonWebTokenDescriptor descriptor)
    {
        switch (audience.ValueKind)
        {
            case JsonValueKind.String:
                AddAudience(descriptor, audience.GetString()!);
                break;

            case JsonValueKind.Array:
                foreach (var element in audience.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException("The 'aud' claim array must contain only strings.");
                    }

                    AddAudience(descriptor, element.GetString()!);
                }

                break;

            default:
                throw new FormatException("The 'aud' claim must be a string or an array of strings.");
        }
    }

    private static void AddAudience(JsonWebTokenDescriptor descriptor, string audience)
    {
        descriptor.Audiences.Add(audience);
        descriptor.Claims.Add(new IdentityClaim(IdentityClaimTypes.Audience, audience));
    }

    private static IdentityClaimValue Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => IdentityClaimValue.FromString(element.GetString()!),
        JsonValueKind.True => IdentityClaimValue.FromBoolean(true),
        JsonValueKind.False => IdentityClaimValue.FromBoolean(false),
        JsonValueKind.Null => IdentityClaimValue.Null,
        JsonValueKind.Number => ConvertNumber(element),
        JsonValueKind.Array => ConvertArray(element),
        JsonValueKind.Object => ConvertObject(element),
        _ => IdentityClaimValue.Null,
    };

    private static IdentityClaimValue ConvertNumber(JsonElement element)
    {
        // Integral wire numbers map to Integer; anything else (fractional, or beyond Int64)
        // lands in double's domain, per the family's pinned numeric normalization rule.
        if (element.TryGetInt64(out var integer))
        {
            return IdentityClaimValue.FromInteger(integer);
        }

        return IdentityClaimValue.FromDouble(element.GetDouble());
    }

    private static IdentityClaimValue ConvertArray(JsonElement element)
    {
        var items = new List<IdentityClaimValue>();
        foreach (var item in element.EnumerateArray())
        {
            items.Add(Convert(item));
        }

        // FromArray enforces IdentityClaimValue.MaxDepth and throws on overflow, which the
        // caller (TryParse) converts to a parse failure.
        return IdentityClaimValue.FromArray(items);
    }

    private static IdentityClaimValue ConvertObject(JsonElement element)
    {
        var members = new List<KeyValuePair<string, IdentityClaimValue>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw DuplicateMember(property.Name, "object");
            }

            members.Add(new KeyValuePair<string, IdentityClaimValue>(property.Name, Convert(property.Value)));
        }

        return IdentityClaimValue.FromObject(members);
    }

    private static byte[] DecodeSegment(string segment, string which)
    {
        try
        {
            return Base64Url.DecodeFromChars(segment.AsSpan());
        }
        catch (FormatException)
        {
            throw new FormatException($"The JSON Web Token {which} segment is not valid base64url.");
        }
    }

    private static JsonDocument ParseJsonObject(byte[] json, string which)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new FormatException($"The JSON Web Token {which} is not valid JSON.");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new FormatException($"The JSON Web Token {which} must be a JSON object.");
        }

        return document;
    }

    private static FormatException DuplicateMember(string name, string which)
        => new($"The JSON Web Token {which} contains a duplicate member '{name}' (RFC 8725 §2.3).");
}
