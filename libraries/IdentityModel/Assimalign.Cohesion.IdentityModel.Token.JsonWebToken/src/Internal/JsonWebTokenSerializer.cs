using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Serializes the typed token model directly with <see cref="Utf8JsonWriter" /> so the signing
/// path remains reflection-free and NativeAOT-safe.
/// </summary>
internal static class JsonWebTokenSerializer
{
    public static string EncodeHeader(JsonWebTokenDescriptor descriptor, string algorithm, string keyId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString(JoseHeaderParameterNames.Algorithm, algorithm);
        writer.WriteString(JoseHeaderParameterNames.KeyId, keyId);

        if (descriptor.TokenType is not null)
        {
            writer.WriteString(JoseHeaderParameterNames.Type, descriptor.TokenType);
        }

        foreach (var (name, value) in descriptor.Header.Parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(descriptor));

            if (string.Equals(name, JoseHeaderParameterNames.Algorithm, StringComparison.Ordinal) ||
                string.Equals(name, JoseHeaderParameterNames.KeyId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The JOSE header parameter '{name}' is owned by the configured token writer.",
                    nameof(descriptor));
            }

            if (string.Equals(name, JoseHeaderParameterNames.Type, StringComparison.Ordinal) &&
                descriptor.TokenType is not null)
            {
                throw new ArgumentException(
                    "The JOSE 'typ' parameter must be supplied through either TokenType or Header.Parameters, not both.",
                    nameof(descriptor));
            }

            if (string.Equals(name, JoseHeaderParameterNames.Base64Payload, StringComparison.Ordinal))
            {
                if (!value.TryGetBoolean(out bool encoded))
                {
                    throw new ArgumentException("The JOSE 'b64' parameter must be a Boolean value.", nameof(descriptor));
                }

                if (!encoded)
                {
                    throw new NotSupportedException("Unencoded JWS payloads (b64:false) are not supported.");
                }
            }

            writer.WritePropertyName(name);
            WriteValue(writer, value, name);
        }

        writer.WriteEndObject();
        writer.Flush();
        return Base64Url.EncodeToString(buffer.WrittenSpan);
    }

    public static string EncodePayload(JsonWebTokenDescriptor descriptor)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        var projectedClaims = new HashSet<string>(StringComparer.Ordinal);

        writer.WriteStartObject();

        if (descriptor.Issuer is not null)
        {
            writer.WriteString(IdentityClaimTypes.Issuer, descriptor.Issuer);
            projectedClaims.Add(IdentityClaimTypes.Issuer);
        }

        if (descriptor.Subject is not null)
        {
            writer.WriteString(IdentityClaimTypes.Subject, descriptor.Subject.Value);
            projectedClaims.Add(IdentityClaimTypes.Subject);
        }

        if (descriptor.Audiences.Count > 0)
        {
            WriteAudiences(writer, descriptor.Audiences);
            projectedClaims.Add(IdentityClaimTypes.Audience);
        }

        if (descriptor.ExpiresAt is { } expiresAt)
        {
            writer.WriteNumber(IdentityClaimTypes.ExpirationTime, expiresAt.ToUnixTimeSeconds());
            projectedClaims.Add(IdentityClaimTypes.ExpirationTime);
        }

        if (descriptor.NotBefore is { } notBefore)
        {
            writer.WriteNumber(IdentityClaimTypes.NotBefore, notBefore.ToUnixTimeSeconds());
            projectedClaims.Add(IdentityClaimTypes.NotBefore);
        }

        if (descriptor.IssuedAt is { } issuedAt)
        {
            writer.WriteNumber(IdentityClaimTypes.IssuedAt, issuedAt.ToUnixTimeSeconds());
            projectedClaims.Add(IdentityClaimTypes.IssuedAt);
        }

        if (descriptor.Id is not null)
        {
            writer.WriteString(IdentityClaimTypes.JwtId, descriptor.Id);
            projectedClaims.Add(IdentityClaimTypes.JwtId);
        }

        WriteClaims(writer, descriptor.Claims, projectedClaims);

        writer.WriteEndObject();
        writer.Flush();
        return Base64Url.EncodeToString(buffer.WrittenSpan);
    }

    private static void WriteAudiences(Utf8JsonWriter writer, IList<string> audiences)
    {
        if (audiences.Count == 1)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audiences[0], nameof(audiences));
            writer.WriteString(IdentityClaimTypes.Audience, audiences[0]);
            return;
        }

        writer.WritePropertyName(IdentityClaimTypes.Audience);
        writer.WriteStartArray();
        for (var index = 0; index < audiences.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audiences[index], nameof(audiences));
            writer.WriteStringValue(audiences[index]);
        }

        writer.WriteEndArray();
    }

    private static void WriteClaims(
        Utf8JsonWriter writer,
        IList<IIdentityClaim> claims,
        HashSet<string> projectedClaims)
    {
        var claimOrder = new List<string>();
        var claimValues = new Dictionary<string, List<IdentityClaimValue>>(StringComparer.Ordinal);

        for (var index = 0; index < claims.Count; index++)
        {
            IIdentityClaim? claim = claims[index];
            if (claim is null)
            {
                throw new ArgumentException("The token claim collection must not contain null entries.", nameof(claims));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(claim.Type, nameof(claims));

            if (projectedClaims.Contains(claim.Type))
            {
                throw new ArgumentException(
                    $"The '{claim.Type}' claim is supplied through both a typed descriptor member and Claims.",
                    nameof(claims));
            }

            if (!claimValues.TryGetValue(claim.Type, out var values))
            {
                values = new List<IdentityClaimValue>();
                claimValues.Add(claim.Type, values);
                claimOrder.Add(claim.Type);
            }

            values.Add(claim.Value);
        }

        foreach (string claimType in claimOrder)
        {
            List<IdentityClaimValue> values = claimValues[claimType];
            writer.WritePropertyName(claimType);

            if (values.Count == 1)
            {
                WriteValue(writer, values[0], claimType);
                continue;
            }

            if (IsSingleValuedRegisteredClaim(claimType))
            {
                throw new ArgumentException(
                    $"The registered '{claimType}' claim must not contain multiple values.",
                    nameof(claims));
            }

            writer.WriteStartArray();
            for (var index = 0; index < values.Count; index++)
            {
                WriteValue(writer, values[index], claimType);
            }

            writer.WriteEndArray();
        }
    }

    private static bool IsSingleValuedRegisteredClaim(string claimType)
        => claimType is IdentityClaimTypes.Issuer
            or IdentityClaimTypes.Subject
            or IdentityClaimTypes.ExpirationTime
            or IdentityClaimTypes.NotBefore
            or IdentityClaimTypes.IssuedAt
            or IdentityClaimTypes.JwtId;

    private static void WriteValue(Utf8JsonWriter writer, IdentityClaimValue value, string member)
    {
        switch (value.Kind)
        {
            case IdentityValueKind.Undefined:
                throw new ArgumentException($"The '{member}' value must not be undefined.", nameof(value));
            case IdentityValueKind.Null:
                writer.WriteNullValue();
                break;
            case IdentityValueKind.String:
                writer.WriteStringValue(value.AsString());
                break;
            case IdentityValueKind.Boolean:
                writer.WriteBooleanValue(value.AsBoolean());
                break;
            case IdentityValueKind.Integer:
                writer.WriteNumberValue(value.AsInteger());
                break;
            case IdentityValueKind.Double:
            {
                double number = value.AsDouble();
                if (!double.IsFinite(number))
                {
                    throw new ArgumentException($"The '{member}' value must be a finite JSON number.", nameof(value));
                }

                writer.WriteNumberValue(number);
                break;
            }
            case IdentityValueKind.Decimal:
                writer.WriteNumberValue(value.AsDecimal());
                break;
            case IdentityValueKind.DateTime:
                writer.WriteStringValue(value.AsDateTime());
                break;
            case IdentityValueKind.Binary:
                writer.WriteBase64StringValue(value.AsBinary().Span);
                break;
            case IdentityValueKind.Array:
                writer.WriteStartArray();
                foreach (IdentityClaimValue item in value.AsArray())
                {
                    WriteValue(writer, item, member);
                }

                writer.WriteEndArray();
                break;
            case IdentityValueKind.Object:
                writer.WriteStartObject();
                foreach (var (name, item) in value.AsObject())
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(value));
                    writer.WritePropertyName(name);
                    WriteValue(writer, item, name);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unknown identity claim value kind.");
        }
    }
}
