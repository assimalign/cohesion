using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// The exception thrown when a connection is reset by the remote peer.
/// </summary>
/// <remarks>
/// A stream of a multiplexed connection is reset one direction at a time: the peer resets its sending
/// direction, which fails this end's reads, or stops this end's sending direction, which fails its writes.
/// When the peer gave a reason in an application error code (see <see cref="IMultiplexedStreamAbort"/>), a
/// driver that reports the reset through this exception carries the code in
/// <see cref="ApplicationErrorCode"/>.
/// </remarks>
public sealed class ConnectionResetException : ConnectionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class.
    /// </summary>
    public ConnectionResetException()
        : base("The connection was reset by the remote peer.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ConnectionResetException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ConnectionResetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class with a specified error message
    /// and the application error code the remote peer reset the stream with.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="applicationErrorCode">The application protocol error code the remote peer sent.</param>
    public ConnectionResetException(string message, long applicationErrorCode)
        : base(message)
    {
        ApplicationErrorCode = applicationErrorCode;
    }

    /// <summary>
    /// Gets the application protocol error code the remote peer reset or stopped the stream with, or
    /// <see langword="null"/> when the reset carried none.
    /// </summary>
    public long? ApplicationErrorCode { get; }
}
