using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A Server-Sent Events id containing no CR, LF, or NUL. The empty string is
/// valid and resets the client's last event id.
/// </summary>
/// <remarks>
/// The WHATWG parser ends a line at CR, LF, or CRLF and does not update the last
/// event id buffer for an <c>id</c> containing NUL. Construction and implicit
/// string conversion reject these characters. <see langword="default"/> holds
/// no value; the formatter omits it, just as it omits a nullable id set to
/// <see langword="null"/>. Use an empty string to emit an id reset.
/// </remarks>
public partial struct ServerSentEventId
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is a single-line id without NUL.
    /// </summary>
    /// <param name="value">The candidate id. The empty string is valid.</param>
    /// <param name="message">The reason for refusal, or <see langword="null"/> when valid.</param>
    /// <returns><see langword="true"/> for a non-null string without CR, LF, or NUL.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is null || value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            // Untrusted text must not reach exception messages or downstream logs.
            message = ErrorMessages.ServerSentEventIdInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Converts optional text to a checked id, preserving <see langword="null"/>.
    /// </summary>
    /// <param name="value">The id text, or <see langword="null"/> to omit the field.</param>
    /// <returns>The checked id, or <see langword="null"/> for absent text.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains CR, LF, or NUL.</exception>
    public static implicit operator ServerSentEventId?(string? value)
    {
        // A conditional expression would convert the null literal through the string constructor.
        if (value is null)
        {
            return null;
        }

        return new ServerSentEventId(value);
    }
}
