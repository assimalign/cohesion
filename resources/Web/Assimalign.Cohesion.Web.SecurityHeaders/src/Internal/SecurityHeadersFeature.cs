using System;
using System.Security.Cryptography;
using System.Threading;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// The default <see cref="ISecurityHeadersFeature"/>: a nonce generated on first read from the
/// operating system's cryptographically secure random number generator and fixed for the exchange.
/// </summary>
internal sealed class SecurityHeadersFeature : ISecurityHeadersFeature
{
    // CSP3 §7.1 ("Nonce Reuse"): a nonce SHOULD carry at least 128 bits of entropy.
    private const int nonceByteCount = 16;

    private string? _nonce;

    public string Name => nameof(ISecurityHeadersFeature);

    public string Nonce
    {
        get
        {
            string? nonce = Volatile.Read(ref _nonce);
            if (nonce is not null)
            {
                return nonce;
            }

            // A handler may read the nonce from more than one task; the first value published wins so
            // every reader, and the header, see the same nonce.
            string generated = Generate();
            return Interlocked.CompareExchange(ref _nonce, generated, null) ?? generated;
        }
    }

    private static string Generate()
    {
        Span<byte> bytes = stackalloc byte[nonceByteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
