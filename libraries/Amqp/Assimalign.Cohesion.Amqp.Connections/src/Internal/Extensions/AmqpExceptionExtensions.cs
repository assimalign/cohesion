using System;
using System.Globalization;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Creates the <see cref="AmqpException"/> instances the codec and the protocol engine throw. The
/// exception root belongs to <c>Assimalign.Cohesion.Amqp</c>, so its creation members live here.
/// </summary>
/// <remarks>
/// A connection that closes, a session that ends or a link that detaches fails every operation waiting
/// on it: those are normal paths, so their messages are read once.
/// </remarks>
internal static class AmqpExceptionExtensions
{
    private static readonly string _connectionClosedMessage = ErrorMessages.ConnectionClosed;
    private static readonly string _connectionClosedWithErrorFormat = ErrorMessages.ConnectionClosedWithError;
    private static readonly string _sessionEndedMessage = ErrorMessages.SessionEnded;
    private static readonly string _sessionEndedWithErrorFormat = ErrorMessages.SessionEndedWithError;
    private static readonly string _linkDetachedMessage = ErrorMessages.LinkDetached;
    private static readonly string _linkDetachedWithErrorFormat = ErrorMessages.LinkDetachedWithError;
    private static readonly string _linkRefusedMessage = ErrorMessages.LinkRefused;

    extension(AmqpException)
    {
        /// <summary>
        /// Creates the exception for a peer that broke the protocol, carrying the AMQP error to report.
        /// </summary>
        public static AmqpException ProtocolViolation(AmqpSymbol condition, string message)
        {
            return new AmqpException(AmqpErrorCode.ProtocolViolation, message)
            {
                Error = new AmqpError(condition, message)
            };
        }

        /// <summary>
        /// Creates the exception for bytes that do not decode as the AMQP type system requires
        /// (<c>amqp:decode-error</c>).
        /// </summary>
        public static AmqpException DecodeError(string message)
        {
            return AmqpException.ProtocolViolation(AmqpErrorCondition.DecodeError, message);
        }

        /// <summary>
        /// Creates the exception for a byte stream that does not form valid frames
        /// (<c>amqp:connection:framing-error</c>).
        /// </summary>
        public static AmqpException FramingError(string message)
        {
            return AmqpException.ProtocolViolation(AmqpErrorCondition.FramingError, message);
        }

        /// <summary>
        /// Creates the exception for an operation on a connection that has closed.
        /// </summary>
        /// <param name="error">The error the <c>close</c> carried, or <see langword="null"/>.</param>
        /// <param name="innerException">What ended the connection when no <c>close</c> did, or <see langword="null"/>.</param>
        public static AmqpException ConnectionClosed(AmqpError? error, Exception? innerException = null)
        {
            string message = error is null
                ? _connectionClosedMessage
                : string.Format(CultureInfo.CurrentCulture, _connectionClosedWithErrorFormat, error.Condition);

            return Create(AmqpErrorCode.ConnectionClosed, message, error, innerException);
        }

        /// <summary>
        /// Creates the exception for an operation on a session that has ended.
        /// </summary>
        /// <param name="error">The error the <c>end</c> carried, or <see langword="null"/>.</param>
        /// <param name="innerException">The connection's exception when its closing ended the session, or <see langword="null"/>.</param>
        public static AmqpException SessionEnded(AmqpError? error, Exception? innerException = null)
        {
            string message = error is null
                ? _sessionEndedMessage
                : string.Format(CultureInfo.CurrentCulture, _sessionEndedWithErrorFormat, error.Condition);

            return Create(AmqpErrorCode.SessionEnded, message, error, innerException);
        }

        /// <summary>
        /// Creates the exception for an operation on a link that has detached.
        /// </summary>
        /// <param name="error">The error the <c>detach</c> carried, or <see langword="null"/>.</param>
        /// <param name="innerException">The session's exception when its ending detached the link, or <see langword="null"/>.</param>
        public static AmqpException LinkDetached(AmqpError? error, Exception? innerException = null)
        {
            string message = error is null
                ? _linkDetachedMessage
                : string.Format(CultureInfo.CurrentCulture, _linkDetachedWithErrorFormat, error.Condition);

            return Create(AmqpErrorCode.LinkDetached, message, error, innerException);
        }

        /// <summary>
        /// Creates the exception for a link the peer refused: it answered the <c>attach</c> without a
        /// terminus and detached.
        /// </summary>
        /// <param name="error">The error the peer's <c>detach</c> carried, or <see langword="null"/>.</param>
        public static AmqpException LinkRefused(AmqpError? error)
        {
            return Create(AmqpErrorCode.LinkDetached, _linkRefusedMessage, error, null);
        }
    }

    private static AmqpException Create(AmqpErrorCode code, string message, AmqpError? error, Exception? innerException)
    {
        return innerException is null
            ? new AmqpException(code, message) { Error = error }
            : new AmqpException(code, message, innerException) { Error = error };
    }
}
