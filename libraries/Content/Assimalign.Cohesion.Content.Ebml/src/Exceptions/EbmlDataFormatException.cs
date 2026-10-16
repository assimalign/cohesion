using System;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>Reports malformed EBML with a format-independent code and absolute position.</summary>
public class EbmlDataFormatException : ContentFormatException
{
    /// <summary>Initializes a legacy, unclassified failure.</summary>
    public EbmlDataFormatException() : base(ErrorMessages.InvalidValue) { }

    /// <summary>Initializes a legacy, unclassified failure.</summary>
    /// <param name="message">The failure message.</param>
    public EbmlDataFormatException(string message) : base(message) { }

    /// <summary>Initializes a legacy failure with a cause.</summary>
    /// <param name="message">The failure message.</param>
    /// <param name="cause">The underlying exception.</param>
    public EbmlDataFormatException(string message, Exception cause) : base(message, cause) { }

    /// <summary>Initializes a classified failure.</summary>
    /// <param name="message">The failure message.</param>
    /// <param name="errorCode">The failure classification.</param>
    /// <param name="position">The absolute byte position.</param>
    /// <param name="cause">An optional underlying exception.</param>
    public EbmlDataFormatException(string message, ContentFormatErrorCode errorCode, long position, Exception? cause = null)
        : base(message, errorCode, position, cause) { }
}
