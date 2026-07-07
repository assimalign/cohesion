namespace Assimalign.Cohesion.Http;

/// <summary>
/// The outcome of verifying content against the algorithms carried by an
/// <see cref="HttpDigestField"/>.
/// </summary>
public enum HttpDigestVerificationResult
{
    /// <summary>
    /// The field carried no algorithm this library can verify with — either it was empty or every
    /// entry used a deprecated/unregistered algorithm. Per RFC 9530 a recipient that cannot verify
    /// treats the digest as absent rather than as a failure.
    /// </summary>
    NoSupportedAlgorithm = 0,

    /// <summary>
    /// Every supported digest the field carried matched the computed digest of the content.
    /// </summary>
    Matched,

    /// <summary>
    /// At least one supported digest the field carried did not match the computed digest of the
    /// content. The content must be treated as corrupt or tampered.
    /// </summary>
    Mismatched,
}
