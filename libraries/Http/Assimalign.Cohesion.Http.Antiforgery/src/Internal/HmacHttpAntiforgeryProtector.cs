using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The default <see cref="IHttpAntiforgeryProtector"/>: attaches an HMAC-SHA256 tag over a
/// single application key. Protected payload = <c>plaintext ‖ HMAC(key, plaintext)</c>;
/// unprotect recomputes the tag and compares it in fixed time before returning the plaintext.
/// </summary>
/// <remarks>
/// This preserves the package's original zero-dependency, single-process behavior. It provides
/// integrity, not confidentiality — the plaintext travels in the clear within the token, exactly
/// as the previous engine did. Restart-stable or multi-node deployments should replace this with
/// a rotating-key-ring-backed protector via <see cref="HttpAntiforgeryOptions.Protector"/>.
/// </remarks>
internal sealed class HmacHttpAntiforgeryProtector : IHttpAntiforgeryProtector
{
    private const int MacBytes = 32;

    private readonly byte[] _key;

    public HmacHttpAntiforgeryProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            throw new ArgumentException("The antiforgery HMAC key must not be empty.", nameof(key));
        }

        _key = key;
    }

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        byte[] result = new byte[plaintext.Length + MacBytes];
        plaintext.CopyTo(result);
        HMACSHA256.HashData(_key, plaintext, result.AsSpan(plaintext.Length));
        return result;
    }

    /// <inheritdoc />
    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, [NotNullWhen(true)] out byte[]? plaintext)
    {
        plaintext = null;
        if (protectedData.Length < MacBytes)
        {
            return false;
        }

        int payloadLength = protectedData.Length - MacBytes;
        ReadOnlySpan<byte> payload = protectedData.Slice(0, payloadLength);
        ReadOnlySpan<byte> mac = protectedData.Slice(payloadLength, MacBytes);

        Span<byte> expected = stackalloc byte[MacBytes];
        HMACSHA256.HashData(_key, payload, expected);

        if (!CryptographicOperations.FixedTimeEquals(mac, expected))
        {
            return false;
        }

        plaintext = payload.ToArray();
        return true;
    }
}
