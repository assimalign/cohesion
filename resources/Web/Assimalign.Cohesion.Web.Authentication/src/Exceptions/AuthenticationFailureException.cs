using System;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The failure carried by a rejected <see cref="AuthenticateResult"/> when a scheme rejects a
/// credential and the caller supplied only a reason string rather than an exception.
/// </summary>
public sealed class AuthenticationFailureException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationFailureException"/> class.
    /// </summary>
    /// <param name="message">The failure reason.</param>
    public AuthenticationFailureException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationFailureException"/> class.
    /// </summary>
    /// <param name="message">The failure reason.</param>
    /// <param name="innerException">The underlying cause.</param>
    public AuthenticationFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
