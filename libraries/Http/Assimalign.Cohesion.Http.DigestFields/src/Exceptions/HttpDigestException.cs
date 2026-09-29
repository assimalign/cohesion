namespace Assimalign.Cohesion.Http;

/// <summary>
/// Thrown when an RFC 9530 digest field cannot be parsed into a well-formed
/// <see cref="HttpDigestField"/> or <see cref="HttpWantDigestField"/> — for example malformed
/// structured-field syntax, a digest member that is not a Byte Sequence, or a preference member
/// that is not an Integer. Inherits the HTTP area exception root <see cref="HttpException"/>.
/// </summary>
/// <remarks>
/// Only the throwing <c>Parse</c> convenience methods raise this; the <c>TryParse</c> methods
/// report the same failures through a <see langword="bool"/> result and an <c>out</c> diagnostic
/// string, and are the preferred entry point on hot parse paths.
/// </remarks>
public sealed class HttpDigestException : HttpException
{
    /// <summary>
    /// Initializes a new <see cref="HttpDigestException"/> with a diagnostic message.
    /// </summary>
    /// <param name="message">A human-readable description of why the digest field was rejected.</param>
    public HttpDigestException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidHeader;
    }
}
