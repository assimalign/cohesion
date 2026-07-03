using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The pluggable cryptographic seam behind antiforgery tokens. Antiforgery frames its own
/// double-submit payloads (a cookie secret and a request nonce bound to it) and delegates the
/// authenticated protection of those payloads to an implementation of this interface, so the
/// key material and its lifecycle live entirely outside this package.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation signs payloads with HMAC-SHA256 over
/// <see cref="HttpAntiforgeryOptions.Key"/> — correct for a single process but tied to a
/// hand-distributed static key with no rotation. Supplying an implementation backed by a
/// persisted, rotating key ring (for example the Cohesion data-protection provider, wired in a
/// <c>*.Hosting</c> project) lets multi-node deployments stop copying raw key bytes: the ring
/// persists and rotates keys, and its versioned payload header selects the right key on
/// unprotect. This package deliberately takes no dependency on any data-protection library; the
/// composition root adapts one to this seam.
/// </para>
/// <para>
/// <see cref="TryUnprotect(ReadOnlySpan{byte}, out byte[])"/> is fed untrusted request input, so
/// it must never throw for a malformed or foreign payload — it returns <see langword="false"/>
/// instead. Implementations must verify integrity in fixed time.
/// </para>
/// </remarks>
public interface IHttpAntiforgeryProtector
{
    /// <summary>
    /// Produces an integrity-protected (and, for encrypting implementations, confidential)
    /// payload that <see cref="TryUnprotect(ReadOnlySpan{byte}, out byte[])"/> can round-trip.
    /// </summary>
    /// <param name="plaintext">The antiforgery payload to protect.</param>
    /// <returns>The protected payload bytes.</returns>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Verifies and recovers a payload previously produced by <see cref="Protect(ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <param name="protectedData">The protected payload. Treated as untrusted input.</param>
    /// <param name="plaintext">
    /// When this method returns <see langword="true"/>, the recovered plaintext; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="protectedData"/> is well-formed and
    /// authentic; otherwise <see langword="false"/>.
    /// </returns>
    bool TryUnprotect(ReadOnlySpan<byte> protectedData, [NotNullWhen(true)] out byte[]? plaintext);
}
