using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The exception thrown when the content of a request to a resource instance, an action's input or
/// a state snapshot, is not a value the instance accepts.
/// </summary>
/// <remarks>
/// A content serializer throws it for content that is not a value of the requested type. An action,
/// or the apply callback of its state, throws it for a value of the right type that the instance
/// refuses. A control plane reports it as <see cref="ApplicationActionStatus.Rejected"/>, with the
/// exception's message as the reason, and changes nothing.
/// </remarks>
public class ApplicationContentException : Exception
{
    /// <summary>
    /// Initializes a new instance with the reason the content was refused.
    /// </summary>
    /// <param name="message">Why the content was refused, for the operator who sent it.</param>
    public ApplicationContentException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the reason the content was refused and the exception that
    /// refused it.
    /// </summary>
    /// <param name="message">Why the content was refused, for the operator who sent it.</param>
    /// <param name="innerException">The exception that refused the content.</param>
    public ApplicationContentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
