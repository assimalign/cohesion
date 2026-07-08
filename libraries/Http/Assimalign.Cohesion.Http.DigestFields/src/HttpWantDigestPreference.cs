namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single preference from an RFC 9530 <c>Want-Content-Digest</c> / <c>Want-Repr-Digest</c>
/// field: an <see cref="HttpDigestAlgorithm"/> paired with the integer preference the peer
/// expressed for it. A preference of <c>0</c> (or below) means the algorithm is not acceptable;
/// higher values indicate greater preference (RFC 9530 &#167; 4).
/// </summary>
public readonly struct HttpWantDigestPreference
{
    /// <summary>
    /// Initializes a new <see cref="HttpWantDigestPreference"/>.
    /// </summary>
    /// <param name="algorithm">The algorithm the preference applies to.</param>
    /// <param name="preference">The integer preference (0 = not acceptable, higher = more preferred).</param>
    public HttpWantDigestPreference(HttpDigestAlgorithm algorithm, long preference)
    {
        Algorithm = algorithm;
        Preference = preference;
    }

    /// <summary>Gets the algorithm the preference applies to.</summary>
    public HttpDigestAlgorithm Algorithm { get; }

    /// <summary>Gets the integer preference; 0 or below means the algorithm is not acceptable.</summary>
    public long Preference { get; }

    /// <summary>Gets a value indicating whether the peer finds this algorithm acceptable (<see cref="Preference"/> &gt; 0).</summary>
    public bool IsAcceptable => Preference > 0;
}
