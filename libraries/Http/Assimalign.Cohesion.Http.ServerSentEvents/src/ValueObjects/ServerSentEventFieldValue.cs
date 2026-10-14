using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single-line Server-Sent Events field value containing no CR, LF, or NUL.
/// The empty string is valid, including for an <c>id</c> that resets the client's
/// last event id.
/// </summary>
/// <remarks>
/// The WHATWG parser ends a line at CR, LF, or CRLF and ignores an <c>id</c>
/// containing NUL. NUL has no legitimate use in an event type or comment either,
/// so the same rule covers all three fields. Construction and implicit string
/// conversion reject these characters before they can inject fields or events.
/// <see langword="default"/> holds no value; the formatter omits it, just as it
/// omits a nullable field set to <see langword="null"/>. Use an empty string to
/// emit an empty <c>id</c> or comment.
/// </remarks>
public partial struct ServerSentEventFieldValue
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is a single-line field value.
    /// </summary>
    /// <param name="value">The candidate field value. The empty string is valid.</param>
    /// <param name="message">The reason for refusal, or <see langword="null"/> when valid.</param>
    /// <returns><see langword="true"/> for a non-null string without CR, LF, or NUL.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is null || value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            // Untrusted text must not reach exception messages or downstream logs.
            message = ErrorMessages.ServerSentEventFieldValueInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Converts optional text to a checked field value, preserving <see langword="null"/>.
    /// </summary>
    /// <param name="value">The field text, or <see langword="null"/> to omit the field.</param>
    /// <returns>The checked field value, or <see langword="null"/> for absent text.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains CR, LF, or NUL.</exception>
    public static implicit operator ServerSentEventFieldValue?(string? value)
    {
        // A conditional expression would convert the null literal through the string constructor.
        if (value is null)
        {
            return null;
        }

        return new ServerSentEventFieldValue(value);
    }
}
