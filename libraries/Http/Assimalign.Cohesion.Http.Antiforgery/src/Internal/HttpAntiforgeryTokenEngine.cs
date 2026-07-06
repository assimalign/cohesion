using System;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Mints and verifies the antiforgery cookie/request token pair using a signed double-submit
/// model. The engine owns only the payload <em>framing</em> (domain separation, the cookie
/// secret, the request nonce, and base64url transport); the underlying authenticated protection
/// is delegated to an <see cref="IHttpAntiforgeryProtector"/>, so key material and rotation live
/// entirely outside this package.
/// </summary>
/// <remarks>
/// <para>
/// The cookie token protects the payload <c>0x01 ‖ secret</c> (where <c>secret</c> is 32
/// cryptographically random bytes); the request token protects <c>0x02 ‖ nonce ‖ secret</c>
/// (where <c>nonce</c> is 16 random bytes). The two are <b>domain-separated</b> by the leading
/// byte so a cookie token can never be replayed as a request token, and the request token binds
/// to one specific cookie secret.
/// </para>
/// <para>
/// Validation recovers each payload through the protector (which fails closed on tampering) and
/// compares the recovered cookie secret in fixed time via
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
/// A forged, truncated, or wrong-key token is a validation failure, never an exception on the
/// read path — <see cref="Base64Url"/> decode failures on untrusted input are caught and mapped
/// to "invalid".
/// </para>
/// </remarks>
internal sealed class HttpAntiforgeryTokenEngine
{
    private const int SecretBytes = 32;
    private const int NonceBytes = 16;
    private const byte CookieDomain = 0x01;
    private const byte RequestDomain = 0x02;

    private readonly IHttpAntiforgeryProtector _protector;

    public HttpAntiforgeryTokenEngine(IHttpAntiforgeryProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
    }

    /// <summary>
    /// Generates a new protected cookie token and returns the underlying secret so a matching
    /// request token can be minted in the same operation.
    /// </summary>
    /// <param name="secret">The 32-byte cookie secret backing the token.</param>
    /// <returns>The base64url cookie token string.</returns>
    public string GenerateCookieToken(out byte[] secret)
    {
        secret = RandomNumberGenerator.GetBytes(SecretBytes);

        Span<byte> payload = stackalloc byte[1 + SecretBytes];
        payload[0] = CookieDomain;
        secret.CopyTo(payload.Slice(1));

        return Base64Url.EncodeToString(_protector.Protect(payload));
    }

    /// <summary>
    /// Validates a cookie token and returns its secret when the token is authentic; otherwise
    /// returns <see langword="null"/>.
    /// </summary>
    /// <param name="token">The base64url cookie token, or <see langword="null"/>.</param>
    /// <returns>The cookie secret when valid; otherwise <see langword="null"/>.</returns>
    public byte[]? ValidateCookieToken(string? token)
    {
        byte[]? raw = TryDecode(token);
        if (raw is null
            || !_protector.TryUnprotect(raw, out byte[]? payload)
            || payload.Length != 1 + SecretBytes
            || payload[0] != CookieDomain)
        {
            return null;
        }

        return payload.AsSpan(1).ToArray();
    }

    /// <summary>
    /// Generates a request token bound to the supplied cookie secret.
    /// </summary>
    /// <param name="cookieSecret">The secret from the paired cookie token.</param>
    /// <returns>The base64url request token string.</returns>
    public string GenerateRequestToken(ReadOnlySpan<byte> cookieSecret)
    {
        Span<byte> payload = stackalloc byte[1 + NonceBytes + SecretBytes];
        payload[0] = RequestDomain;
        RandomNumberGenerator.Fill(payload.Slice(1, NonceBytes));
        cookieSecret.CopyTo(payload.Slice(1 + NonceBytes));

        return Base64Url.EncodeToString(_protector.Protect(payload));
    }

    /// <summary>
    /// Validates a request token against the supplied cookie secret.
    /// </summary>
    /// <param name="token">The base64url request token, or <see langword="null"/>.</param>
    /// <param name="cookieSecret">The secret from the validated cookie token.</param>
    /// <returns><see langword="true"/> when the request token is well-formed, authentic, and
    /// bound to <paramref name="cookieSecret"/>.</returns>
    public bool ValidateRequestToken(string? token, ReadOnlySpan<byte> cookieSecret)
    {
        byte[]? raw = TryDecode(token);
        if (raw is null
            || !_protector.TryUnprotect(raw, out byte[]? payload)
            || payload.Length != 1 + NonceBytes + SecretBytes
            || payload[0] != RequestDomain)
        {
            return false;
        }

        ReadOnlySpan<byte> boundSecret = payload.AsSpan(1 + NonceBytes);
        return CryptographicOperations.FixedTimeEquals(boundSecret, cookieSecret);
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
            // Untrusted input — a malformed token is a validation failure, not an exceptional
            // condition for the caller.
            return null;
        }
    }
}
