using System;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Web.Sessions.Internal;

/// <summary>
/// Mints session identifiers. Ids are 128 bits of cryptographically strong
/// randomness (<see cref="RandomNumberGenerator"/>) encoded as URL-safe,
/// unpadded base64url — an unguessable, cookie-safe token with no structure an
/// attacker can predict or forge.
/// </summary>
internal static class SessionId
{
    // 128 bits of entropy: the OWASP session-id floor. base64url of 16 bytes is
    // 22 characters, all within the RFC 6265 cookie-octet grammar.
    private const int EntropyByteCount = 16;

    /// <summary>
    /// Creates a new cryptographically random, URL-safe session identifier.
    /// </summary>
    /// <returns>A 22-character base64url token carrying 128 bits of entropy.</returns>
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[EntropyByteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
