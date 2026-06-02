using System;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Mints and verifies the antiforgery cookie/request token pair using a
/// signed double-submit model.
/// </summary>
/// <remarks>
/// <para>
/// The cookie token is <c>base64url(secret ‖ HMAC(key, 0x01 ‖ secret))</c>
/// where <c>secret</c> is 32 cryptographically random bytes. Signing the
/// cookie token prevents an attacker from injecting an arbitrary cookie value
/// ("cookie tossing").
/// </para>
/// <para>
/// The request token is <c>base64url(nonce ‖ HMAC(key, 0x02 ‖ nonce ‖ secret))</c>
/// where <c>nonce</c> is 16 random bytes and <c>secret</c> is the cookie
/// token's secret. The HMAC binds the request token to one specific cookie
/// token and cannot be forged without the application key. The two HMAC uses
/// are domain-separated by a leading byte (<c>0x01</c> cookie, <c>0x02</c>
/// request) so a cookie token can never be replayed as a request token.
/// </para>
/// <para>
/// Verification recomputes the HMAC and compares it in fixed time via
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
/// to avoid timing side channels. All primitives are BCL
/// <see cref="System.Security.Cryptography"/>; the engine is allocation-light
/// (stack buffers for the small token payloads) and AOT/trim safe.
/// </para>
/// </remarks>
internal sealed class HttpAntiforgeryTokenEngine
{
    private const int SecretBytes = 32;
    private const int NonceBytes = 16;
    private const int MacBytes = 32;
    private const byte CookieDomain = 0x01;
    private const byte RequestDomain = 0x02;

    private readonly byte[] _key;

    public HttpAntiforgeryTokenEngine(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            throw new ArgumentException("The antiforgery HMAC key must not be empty.", nameof(key));
        }

        _key = key;
    }

    /// <summary>
    /// Generates a new signed cookie token and returns the underlying secret
    /// so a matching request token can be minted in the same operation.
    /// </summary>
    /// <param name="secret">The 32-byte cookie secret backing the token.</param>
    /// <returns>The base64url cookie token string.</returns>
    public string GenerateCookieToken(out byte[] secret)
    {
        secret = RandomNumberGenerator.GetBytes(SecretBytes);

        Span<byte> token = stackalloc byte[SecretBytes + MacBytes];
        secret.CopyTo(token);
        ComputeMac(CookieDomain, secret, token.Slice(SecretBytes));

        return Base64Url.EncodeToString(token);
    }

    /// <summary>
    /// Validates a cookie token and returns its secret when the signature
    /// checks out; otherwise returns <see langword="null"/>.
    /// </summary>
    /// <param name="token">The base64url cookie token, or <see langword="null"/>.</param>
    /// <returns>The cookie secret when valid; otherwise <see langword="null"/>.</returns>
    public byte[]? ValidateCookieToken(string? token)
    {
        byte[]? raw = TryDecode(token);
        if (raw is null || raw.Length != SecretBytes + MacBytes)
        {
            return null;
        }

        ReadOnlySpan<byte> secret = raw.AsSpan(0, SecretBytes);
        ReadOnlySpan<byte> mac = raw.AsSpan(SecretBytes, MacBytes);

        Span<byte> expected = stackalloc byte[MacBytes];
        ComputeMac(CookieDomain, secret, expected);

        return CryptographicOperations.FixedTimeEquals(mac, expected)
            ? secret.ToArray()
            : null;
    }

    /// <summary>
    /// Generates a request token bound to the supplied cookie secret.
    /// </summary>
    /// <param name="cookieSecret">The secret from the paired cookie token.</param>
    /// <returns>The base64url request token string.</returns>
    public string GenerateRequestToken(ReadOnlySpan<byte> cookieSecret)
    {
        Span<byte> nonce = stackalloc byte[NonceBytes];
        RandomNumberGenerator.Fill(nonce);

        Span<byte> payload = stackalloc byte[NonceBytes + SecretBytes];
        nonce.CopyTo(payload);
        cookieSecret.CopyTo(payload.Slice(NonceBytes));

        Span<byte> token = stackalloc byte[NonceBytes + MacBytes];
        nonce.CopyTo(token);
        ComputeMac(RequestDomain, payload, token.Slice(NonceBytes));

        return Base64Url.EncodeToString(token);
    }

    /// <summary>
    /// Validates a request token against the supplied cookie secret.
    /// </summary>
    /// <param name="token">The base64url request token, or <see langword="null"/>.</param>
    /// <param name="cookieSecret">The secret from the validated cookie token.</param>
    /// <returns><see langword="true"/> when the request token is well-formed,
    /// correctly signed, and bound to <paramref name="cookieSecret"/>.</returns>
    public bool ValidateRequestToken(string? token, ReadOnlySpan<byte> cookieSecret)
    {
        byte[]? raw = TryDecode(token);
        if (raw is null || raw.Length != NonceBytes + MacBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> nonce = raw.AsSpan(0, NonceBytes);
        ReadOnlySpan<byte> mac = raw.AsSpan(NonceBytes, MacBytes);

        Span<byte> payload = stackalloc byte[NonceBytes + SecretBytes];
        nonce.CopyTo(payload);
        cookieSecret.CopyTo(payload.Slice(NonceBytes));

        Span<byte> expected = stackalloc byte[MacBytes];
        ComputeMac(RequestDomain, payload, expected);

        return CryptographicOperations.FixedTimeEquals(mac, expected);
    }

    private void ComputeMac(byte domain, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        Span<byte> buffer = stackalloc byte[1 + data.Length];
        buffer[0] = domain;
        data.CopyTo(buffer.Slice(1));
        HMACSHA256.HashData(_key, buffer, destination);
    }

    private static byte[]? TryDecode(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return Base64Url.DecodeFromChars(token);
        }
        catch (FormatException)
        {
            // Untrusted input — a malformed token is a validation failure,
            // not an exceptional condition for the caller.
            return null;
        }
    }
}
