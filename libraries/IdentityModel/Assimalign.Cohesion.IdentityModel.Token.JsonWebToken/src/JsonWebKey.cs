using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents the public members of one JSON Web Key (RFC 7517) that JOSE signature verification and
/// RFC 7638 thumbprints use.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is structural: <see cref="TryParse(ReadOnlySpan{byte}, out JsonWebKey?)"/> accepts any
/// JSON object and captures the string-valued <c>kty</c>, <c>crv</c>, <c>x</c>, <c>y</c>, <c>n</c>,
/// <c>e</c>, <c>kid</c>, <c>alg</c>, and <c>use</c> members; a registered member with a non-string
/// value is treated as absent. Other members, including private-key members, are never captured. Policy
/// is applied separately: <see cref="CreateECDsa"/> requires a usable EC public key,
/// <see cref="TryValidateEcdsaVerificationKey(string, out string?)"/> applies the lenient rule a
/// <c>kid</c>-selected verification key must meet, and <see cref="TryValidateEs256SigningKey(out string?)"/>
/// applies the strict public ES256 signing-key profile that Cohesion trusted issuers use.
/// </para>
/// <para>
/// The type holds only public key material, so instances need no disposal. Every ECDSA instance
/// returned by <see cref="CreateECDsa"/> is owned and disposed by the caller.
/// </para>
/// </remarks>
public sealed class JsonWebKey
{
    private const string keyTypeMember = "kty";
    private const string curveMember = "crv";
    private const string xMember = "x";
    private const string yMember = "y";
    private const string modulusMember = "n";
    private const string exponentMember = "e";
    private const string keyIdMember = "kid";
    private const string algorithmMember = "alg";
    private const string useMember = "use";
    private const string ellipticCurveKeyType = "EC";
    private const string rsaKeyType = "RSA";
    private const string p256Curve = "P-256";
    private const string p384Curve = "P-384";
    private const string p521Curve = "P-521";
    private const string signatureUse = "sig";
    private const int p256CoordinateLength = 32;

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private readonly string[] _memberNames;

    private JsonWebKey(Dictionary<string, string?> members, string[] memberNames)
    {
        _memberNames = memberNames;
        KeyType = Get(members, keyTypeMember);
        Curve = Get(members, curveMember);
        X = Get(members, xMember);
        Y = Get(members, yMember);
        Modulus = Get(members, modulusMember);
        Exponent = Get(members, exponentMember);
        KeyId = Get(members, keyIdMember);
        Algorithm = Get(members, algorithmMember);
        Use = Get(members, useMember);
    }

    /// <summary>Gets the key type (<c>kty</c>), such as <c>EC</c> or <c>RSA</c>.</summary>
    public string? KeyType { get; }

    /// <summary>Gets the elliptic curve name (<c>crv</c>), such as <c>P-256</c>.</summary>
    public string? Curve { get; }

    /// <summary>Gets the base64url-encoded elliptic-curve x coordinate (<c>x</c>).</summary>
    public string? X { get; }

    /// <summary>Gets the base64url-encoded elliptic-curve y coordinate (<c>y</c>).</summary>
    public string? Y { get; }

    /// <summary>Gets the base64url-encoded RSA modulus (<c>n</c>).</summary>
    public string? Modulus { get; }

    /// <summary>Gets the base64url-encoded RSA public exponent (<c>e</c>).</summary>
    public string? Exponent { get; }

    /// <summary>Gets the key identifier (<c>kid</c>).</summary>
    public string? KeyId { get; }

    /// <summary>Gets the intended JOSE algorithm (<c>alg</c>).</summary>
    public string? Algorithm { get; }

    /// <summary>Gets the intended public key use (<c>use</c>).</summary>
    public string? Use { get; }

    /// <summary>Attempts to parse one JSON Web Key object from UTF-8 JSON.</summary>
    /// <param name="utf8Json">The UTF-8 encoded JSON document, which must be exactly one JSON object.</param>
    /// <param name="key">When this method returns <see langword="true"/>, the parsed key.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="utf8Json"/> is one well-formed JSON object;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out JsonWebKey? key)
    {
        key = null;
        if (utf8Json.StartsWith(Utf8ByteOrderMark))
        {
            // JsonDocument.Parse tolerates a leading UTF-8 byte order mark; the reader does not.
            utf8Json = utf8Json[Utf8ByteOrderMark.Length..];
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            if (reader.Read())
            {
                // A JWK document holds exactly one value; trailing content is malformed.
                return false;
            }

            return TryParse(document.RootElement, out key);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Attempts to read one JSON Web Key from a parsed JSON element.</summary>
    /// <param name="element">The JSON element holding the key.</param>
    /// <param name="key">When this method returns <see langword="true"/>, the parsed key.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="element"/> is a JSON object; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    public static bool TryParse(JsonElement element, [NotNullWhen(true)] out JsonWebKey? key)
    {
        key = null;
        if (element.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        var members = new Dictionary<string, string?>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            names.Add(property.Name);
            if (IsCapturedMember(property.Name))
            {
                // A repeated member resolves to its last occurrence, as JsonElement lookup does.
                members[property.Name] = property.Value.ValueKind is JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
        }

        key = new JsonWebKey(members, names.ToArray());
        return true;
    }

    /// <summary>Computes the RFC 7638 JSON Web Key thumbprint using SHA-256.</summary>
    /// <returns>The base64url-encoded SHA-256 digest of the key's canonical required members.</returns>
    /// <exception cref="InvalidOperationException">A required member for the key type is absent.</exception>
    /// <exception cref="NotSupportedException">The key type is neither <c>EC</c> nor <c>RSA</c>.</exception>
    /// <remarks>
    /// The canonical form is the lexicographically ordered required members without whitespace:
    /// <c>crv</c>, <c>kty</c>, <c>x</c>, <c>y</c> for EC keys and <c>e</c>, <c>kty</c>, <c>n</c> for RSA keys.
    /// </remarks>
    public string ComputeThumbprint()
    {
        (string Name, string Value)[] required = KeyType switch
        {
            ellipticCurveKeyType =>
            [
                (curveMember, Require(Curve, curveMember)),
                (keyTypeMember, ellipticCurveKeyType),
                (xMember, Require(X, xMember)),
                (yMember, Require(Y, yMember)),
            ],
            rsaKeyType =>
            [
                (exponentMember, Require(Exponent, exponentMember)),
                (keyTypeMember, rsaKeyType),
                (modulusMember, Require(Modulus, modulusMember)),
            ],
            _ => throw new NotSupportedException(
                $"RFC 7638 thumbprints are supported for EC and RSA keys, not key type '{KeyType}'."),
        };

        var canonical = new ArrayBufferWriter<byte>();
        // RFC 7638 hashes UTF-8 JSON with no insignificant whitespace; relaxed escaping keeps
        // non-ASCII text as UTF-8 rather than \u escapes. Registered base64url and curve values
        // contain no character either encoder would escape.
        using (var writer = new Utf8JsonWriter(
            canonical,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            for (int index = 0; index < required.Length; index++)
            {
                writer.WriteString(required[index].Name, required[index].Value);
            }

            writer.WriteEndObject();
        }

        byte[] hash = SHA256.HashData(canonical.WrittenSpan);
        try
        {
            return Base64Url.EncodeToString(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>Creates an ECDSA public key from this EC JSON Web Key.</summary>
    /// <returns>A new ECDSA instance holding only the public point. The caller owns and disposes it.</returns>
    /// <exception cref="InvalidOperationException">
    /// The key is not an EC key, names a curve other than <c>P-256</c>, <c>P-384</c>, or <c>P-521</c>,
    /// or lacks a coordinate.
    /// </exception>
    /// <exception cref="FormatException">A coordinate is not valid base64url.</exception>
    /// <exception cref="CryptographicException">
    /// The coordinates are not a valid point on the curve, on platforms whose provider reports it this way.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The coordinates are not a valid point on the curve, on platforms (such as Windows CNG) whose provider
    /// reports an invalid point as unsupported parameters.
    /// </exception>
    public ECDsa CreateECDsa()
    {
        if (!string.Equals(KeyType, ellipticCurveKeyType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"An ECDSA key requires an EC JSON Web Key, not key type '{KeyType}'.");
        }

        ECCurve curve = Curve switch
        {
            p256Curve => ECCurve.NamedCurves.nistP256,
            p384Curve => ECCurve.NamedCurves.nistP384,
            p521Curve => ECCurve.NamedCurves.nistP521,
            _ => throw new InvalidOperationException(
                $"The EC JSON Web Key curve '{Curve}' is not a JOSE ECDSA curve."),
        };
        string encodedX = Require(X, xMember);
        string encodedY = Require(Y, yMember);

        byte[] x = Base64Url.DecodeFromChars(encodedX);
        byte[]? y = null;
        try
        {
            y = Base64Url.DecodeFromChars(encodedY);
            return ECDsa.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint { X = x, Y = y },
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            if (y is not null)
            {
                CryptographicOperations.ZeroMemory(y);
            }
        }
    }

    /// <summary>
    /// Validates that this key can verify ECDSA signatures selected by <c>kid</c>: an EC public key on a
    /// named curve that carries a key identifier and a valid public point.
    /// </summary>
    /// <param name="curve">The required JOSE curve name, such as <c>P-256</c>.</param>
    /// <param name="failure">When this method returns <see langword="false"/>, a description of the first violated rule.</param>
    /// <returns><see langword="true"/> when every rule holds; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="curve"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <remarks>
    /// This is the lenient verification-key rule: <c>kty</c> must be <c>EC</c>, <c>crv</c> must equal
    /// <paramref name="curve"/>, <c>kid</c> must be present as a string (any value, including empty), and
    /// <see cref="CreateECDsa"/> must succeed. Other members, <c>alg</c>, <c>use</c>, and the relation between
    /// <c>kid</c> and the key's thumbprint are not examined; <see cref="TryValidateEs256SigningKey(out string?)"/>
    /// is the strict rule.
    /// </remarks>
    public bool TryValidateEcdsaVerificationKey(string curve, [NotNullWhen(false)] out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(curve);

        if (!string.Equals(KeyType, ellipticCurveKeyType, StringComparison.Ordinal) ||
            !string.Equals(Curve, curve, StringComparison.Ordinal))
        {
            failure = $"A JSON Web Key must be an EC {curve} key.";
            return false;
        }

        if (KeyId is null)
        {
            failure = "A JSON Web Key requires a kid.";
            return false;
        }

        try
        {
            using ECDsa _ = CreateECDsa();
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException or InvalidOperationException or
                PlatformNotSupportedException)
        {
            failure = $"A JSON Web Key is not a valid EC {curve} public key.";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Validates that this key is a public ES256 signing key in the strict form Cohesion trusted issuers
    /// publish.
    /// </summary>
    /// <param name="failure">When this method returns <see langword="false"/>, a description of the first violated rule.</param>
    /// <returns><see langword="true"/> when every rule holds; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// The key must contain exactly the non-blank string members <c>kty</c>=<c>EC</c>,
    /// <c>crv</c>=<c>P-256</c>, <c>x</c>, <c>y</c>, <c>kid</c>, <c>alg</c>=<c>ES256</c>, and
    /// <c>use</c>=<c>sig</c>, with no other or repeated member; both coordinates must decode to 32 octets
    /// forming a valid P-256 point; and <c>kid</c> must equal the key's RFC 7638 thumbprint.
    /// </remarks>
    public bool TryValidateEs256SigningKey([NotNullWhen(false)] out string? failure)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < _memberNames.Length; index++)
        {
            string name = _memberNames[index];
            if (!seen.Add(name) ||
                name is not (keyTypeMember or curveMember or xMember or yMember or keyIdMember or algorithmMember or useMember))
            {
                failure = $"A JSON Web Key contains unsupported or duplicate member '{name}'.";
                return false;
            }
        }

        if (!HasValue(KeyType, keyTypeMember, out failure) ||
            !HasValue(Curve, curveMember, out failure) ||
            !HasValue(X, xMember, out failure) ||
            !HasValue(Y, yMember, out failure) ||
            !HasValue(KeyId, keyIdMember, out failure) ||
            !HasValue(Algorithm, algorithmMember, out failure) ||
            !HasValue(Use, useMember, out failure))
        {
            return false;
        }

        if (!string.Equals(KeyType, ellipticCurveKeyType, StringComparison.Ordinal) ||
            !string.Equals(Curve, p256Curve, StringComparison.Ordinal) ||
            !string.Equals(Algorithm, JoseAlgorithms.ES256, StringComparison.Ordinal) ||
            !string.Equals(Use, signatureUse, StringComparison.Ordinal))
        {
            failure = "A JSON Web Key must be an EC P-256 key for ES256 signatures.";
            return false;
        }

        byte[] x;
        byte[] y;
        try
        {
            x = Base64Url.DecodeFromChars(X!);
        }
        catch (FormatException)
        {
            failure = "A JSON Web Key has invalid base64url coordinates.";
            return false;
        }

        try
        {
            y = Base64Url.DecodeFromChars(Y!);
        }
        catch (FormatException)
        {
            CryptographicOperations.ZeroMemory(x);
            failure = "A JSON Web Key has invalid base64url coordinates.";
            return false;
        }

        try
        {
            if (x.Length != p256CoordinateLength || y.Length != p256CoordinateLength)
            {
                failure = "A P-256 JSON Web Key must have 32-byte coordinates.";
                return false;
            }

            try
            {
                using ECDsa point = ECDsa.Create();
                point.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = x, Y = y },
                });
            }
            catch (Exception exception) when (
                exception is ArgumentException or CryptographicException or PlatformNotSupportedException)
            {
                failure = "A JSON Web Key is not a valid P-256 point.";
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(y);
        }

        if (!string.Equals(KeyId, ComputeThumbprint(), StringComparison.Ordinal))
        {
            failure = "A JSON Web Key identifier must equal its RFC 7638 thumbprint.";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool IsCapturedMember(string name) => name is keyTypeMember or curveMember or xMember or
        yMember or modulusMember or exponentMember or keyIdMember or algorithmMember or useMember;

    private static string? Get(Dictionary<string, string?> members, string name) =>
        members.TryGetValue(name, out string? value) ? value : null;

    private static string Require(string? value, string name) => value
        ?? throw new InvalidOperationException($"The JSON Web Key requires string member '{name}'.");

    private static bool HasValue(string? value, string name, [NotNullWhen(false)] out string? failure)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failure = $"A JSON Web Key requires string member '{name}'.";
            return false;
        }

        failure = null;
        return true;
    }
}
