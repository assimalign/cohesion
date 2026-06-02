using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Thrown when a request fails antiforgery validation &#8212; the required
/// antiforgery token was missing, malformed, unsigned, or not bound to the
/// presented cookie token.
/// </summary>
/// <remarks>
/// Surfaced by <see cref="IHttpAntiforgery.ValidateRequestAsync"/>. Scoped to
/// the HTTP area through the <see cref="HttpException"/> root rather than a
/// framework-wide exception ancestry.
/// </remarks>
public sealed class AntiforgeryValidationException : HttpException
{
    /// <summary>
    /// Initializes a new antiforgery validation failure.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public AntiforgeryValidationException(string message)
        : base(message)
    {
        Code = HttpErrorCode.ProtocolViolation;
    }

    /// <summary>
    /// Initializes a new antiforgery validation failure with an inner cause.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="inner">The inner exception.</param>
    public AntiforgeryValidationException(string message, Exception inner)
        : base(message, inner)
    {
        Code = HttpErrorCode.ProtocolViolation;
    }
}
