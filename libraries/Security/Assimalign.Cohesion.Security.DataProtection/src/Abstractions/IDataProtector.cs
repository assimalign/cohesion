using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Protects and unprotects opaque byte payloads under a specific purpose. Protection is
/// authenticated encryption (AES-256-GCM) keyed by a subkey derived, via HKDF-SHA256, from
/// the key ring's active key and this protector's purpose chain. Every protected payload
/// carries a versioned header naming the key that produced it, so the ring can rotate keys
/// while older payloads still unprotect during their grace window.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IDataProtector"/> extends <see cref="IDataProtectionProvider"/>: calling
/// <see cref="IDataProtectionProvider.CreateProtector(string)"/> on a protector derives a
/// further-scoped child protector, so purposes compose into a chain
/// (<c>provider.CreateProtector("a").CreateProtector("b")</c>). The
/// <see cref="DataProtectionProviderExtensions"/> convenience overload builds a chain in one call.
/// </para>
/// <para>
/// Protection does not itself provide replay or expiration semantics; callers that need
/// time-limited payloads embed and check their own timestamp in the plaintext.
/// </para>
/// </remarks>
public interface IDataProtector : IDataProtectionProvider
{
    /// <summary>
    /// Protects <paramref name="plaintext"/>, returning a self-describing payload that can be
    /// round-tripped by <see cref="Unprotect(ReadOnlySpan{byte})"/> on any node sharing this
    /// protector's key ring and purpose.
    /// </summary>
    /// <param name="plaintext">The data to protect. May be empty.</param>
    /// <returns>
    /// A newly allocated protected payload: a version byte, the producing key's id, a random
    /// nonce, the ciphertext, and the GCM authentication tag.
    /// </returns>
    /// <exception cref="DataProtectionException">
    /// The key ring has no usable active key (for example its repository is unreadable).
    /// </exception>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Verifies and decrypts a payload previously produced by <see cref="Protect(ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <param name="protectedData">The protected payload. Treated as untrusted input.</param>
    /// <returns>The original plaintext.</returns>
    /// <exception cref="DataProtectionException">
    /// The payload is malformed, its authentication tag does not verify, it was produced for a
    /// different purpose, or the key that produced it is unknown, revoked, or aged out of the
    /// unprotect grace window.
    /// </exception>
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}
