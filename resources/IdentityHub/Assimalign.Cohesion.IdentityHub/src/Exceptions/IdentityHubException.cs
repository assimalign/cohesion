using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The exception root of the IdentityHub area. Every failure the area raises on its own behalf
/// derives from it and carries an <see cref="IdentityHubErrorCode"/>.
/// </summary>
/// <remarks>
/// Invalid arguments are still reported with the framework's argument exceptions, and a
/// conditional write that loses a race is an <see cref="IdentityHubWriteResult"/>, not an
/// exception. Domain-invariant failures of the shared identity contracts keep their own root,
/// <c>IdentityModelException</c>.
/// </remarks>
public abstract class IdentityHubException : Exception
{
    /// <summary>
    /// Initializes the exception with its category and message.
    /// </summary>
    /// <param name="code">The failure's category.</param>
    /// <param name="message">The message that describes the failure.</param>
    protected IdentityHubException(IdentityHubErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Initializes the exception with its category, message, and cause.
    /// </summary>
    /// <param name="code">The failure's category.</param>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="innerException">The exception that caused the failure.</param>
    protected IdentityHubException(IdentityHubErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the failure's category.
    /// </summary>
    public IdentityHubErrorCode Code { get; }
}
