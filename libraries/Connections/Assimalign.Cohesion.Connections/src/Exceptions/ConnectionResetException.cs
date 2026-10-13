using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// The exception thrown when a connection is reset by the remote peer.
/// </summary>
/// <remarks>
/// A multiplexed stream can fail because the peer reset either stream direction or closed its parent
/// connection. A sending-direction reset fails this end's reads; stopping this end's sending direction
/// fails its writes. A parent connection loss fails both directions of its open streams. When the
/// transport carries an application error code for the reset or connection close, a driver that reports
/// the failure through this exception carries that code in <see cref="ApplicationErrorCode"/>.
/// </remarks>
public sealed class ConnectionResetException : ConnectionException
{
    // Teardown and cancellation can be normal paths; resolve resource text once.
    private static readonly string _defaultMessage = ErrorMessages.ConnectionResetByPeer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class.
    /// </summary>
    public ConnectionResetException()
        : base(_defaultMessage)
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
    /// and the application error code the remote peer sent with a stream reset or connection close.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="applicationErrorCode">The application protocol error code the remote peer sent.</param>
    public ConnectionResetException(string message, long applicationErrorCode)
        : base(message)
    {
        ApplicationErrorCode = applicationErrorCode;
    }

    /// <summary>
    /// Gets the application protocol error code the remote peer sent when resetting or stopping the
    /// stream, or closing its parent connection; <see langword="null"/> when the failure carried none.
    /// </summary>
    public long? ApplicationErrorCode { get; }
}
