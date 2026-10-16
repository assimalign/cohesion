using System;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Thrown when content is malformed for its format: truncated input, invalid structure, or values a
/// format's specification forbids. Format packages throw this (or a derived type) from their readers
/// rather than leaking raw parsing exceptions.
/// </summary>
public class ContentFormatException : ContentException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContentFormatException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the malformed input.</param>
    public ContentFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentFormatException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the malformed input.</param>
    /// <param name="position">The byte or character position at which the malformed input was detected.</param>
    public ContentFormatException(string message, long position)
        : base(message)
    {
        Position = position;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentFormatException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the malformed input.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public ContentFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a classified content format failure.</summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="errorCode">The format-independent failure classification.</param>
    /// <param name="position">The absolute byte or character position, when known.</param>
    /// <param name="innerException">The underlying failure, when present.</param>
    public ContentFormatException(string message, ContentFormatErrorCode errorCode, long? position = null, Exception? innerException = null)
        : base(message, innerException!)
    {
        ErrorCode = errorCode;
        Position = position;
    }

    /// <summary>Gets the failure classification; legacy constructors use <see cref="ContentFormatErrorCode.Unspecified"/>.</summary>
    public ContentFormatErrorCode ErrorCode { get; } = ContentFormatErrorCode.Unspecified;

    /// <summary>Gets the byte or character position at which the malformed input was detected, when known.</summary>
    public long? Position { get; }
}
