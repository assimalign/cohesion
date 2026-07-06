using System;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// The root exception for the Content library family. Format packages derive their own exception types
/// from this root so callers can catch content failures at area granularity.
/// </summary>
public class ContentException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContentException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ContentException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public ContentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
