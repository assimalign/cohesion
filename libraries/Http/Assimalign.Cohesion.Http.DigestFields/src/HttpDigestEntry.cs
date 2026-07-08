using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single entry of an RFC 9530 digest field: an <see cref="HttpDigestAlgorithm"/> paired with
/// the raw (already base64-decoded) digest bytes carried for it. Order within a
/// <see cref="HttpDigestField"/> mirrors the field's dictionary order.
/// </summary>
/// <remarks>
/// Entries for deprecated algorithms (<see cref="HttpDigestAlgorithm.IsSupported"/> is
/// <see langword="false"/>) and for unregistered algorithm keys are preserved so a recipient sees
/// every offered digest; the verification path simply skips the ones it cannot compute.
/// </remarks>
public readonly struct HttpDigestEntry
{
    /// <summary>
    /// Initializes a new <see cref="HttpDigestEntry"/>.
    /// </summary>
    /// <param name="algorithm">The algorithm the digest was computed with.</param>
    /// <param name="digest">The raw digest bytes (base64-decoded).</param>
    public HttpDigestEntry(HttpDigestAlgorithm algorithm, ReadOnlyMemory<byte> digest)
    {
        Algorithm = algorithm;
        Digest = digest;
    }

    /// <summary>Gets the algorithm the digest was computed with.</summary>
    public HttpDigestAlgorithm Algorithm { get; }

    /// <summary>Gets the raw digest bytes (base64-decoded on parse; base64-encoded on serialize).</summary>
    public ReadOnlyMemory<byte> Digest { get; }
}
