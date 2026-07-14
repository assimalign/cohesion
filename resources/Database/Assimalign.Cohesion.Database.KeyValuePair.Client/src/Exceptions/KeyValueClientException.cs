using System;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// Thrown when a key-value client operation fails. Maps the shared client core's
/// wire-coded failures onto a key-value-scoped <see cref="KeyValueClientErrorKind"/>
/// taxonomy while preserving the underlying <see cref="ProtocolErrorCode"/>.
/// </summary>
/// <remarks>
/// Conditional misses (compare-and-swap) never surface here — they are
/// first-class outcomes on the connection surface. What does surface as
/// <see cref="KeyValueClientErrorKind.ExecutionFailure"/> includes the engine's
/// retryable first-updater-wins write conflict (a concurrently committed change
/// to the same key); callers retry the command.
/// </remarks>
public sealed class KeyValueClientException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueClientException"/>.
    /// </summary>
    /// <param name="kind">The key-value-scoped failure category.</param>
    /// <param name="code">The underlying wire error code.</param>
    /// <param name="message">The error message.</param>
    public KeyValueClientException(KeyValueClientErrorKind kind, ProtocolErrorCode code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>
    /// Initializes a new <see cref="KeyValueClientException"/> with an inner exception.
    /// </summary>
    /// <param name="kind">The key-value-scoped failure category.</param>
    /// <param name="code">The underlying wire error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public KeyValueClientException(KeyValueClientErrorKind kind, ProtocolErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>
    /// Gets the key-value-scoped failure category.
    /// </summary>
    public KeyValueClientErrorKind Kind { get; }

    /// <summary>
    /// Gets the underlying wire error code.
    /// </summary>
    public ProtocolErrorCode Code { get; }

    /// <summary>
    /// Indicates whether the failure left the connection usable. Command-level
    /// failures (parse, execution, transaction abort) keep the session ready;
    /// connection, protocol, and capacity failures break it.
    /// </summary>
    public bool ConnectionUsable => Kind is KeyValueClientErrorKind.ParseFailure
        or KeyValueClientErrorKind.ExecutionFailure
        or KeyValueClientErrorKind.TransactionAborted
        or KeyValueClientErrorKind.NotAuthorized
        or KeyValueClientErrorKind.MalformedResult;

    /// <summary>
    /// Translates a shared-core <see cref="DatabaseClientException"/> into a
    /// key-value-scoped exception, mapping its wire code onto a
    /// <see cref="KeyValueClientErrorKind"/>.
    /// </summary>
    /// <param name="exception">The core exception to translate.</param>
    /// <returns>The key-value-scoped exception.</returns>
    internal static KeyValueClientException FromClientException(DatabaseClientException exception)
    {
        KeyValueClientErrorKind kind = exception.Code switch
        {
            ProtocolErrorCode.UnsupportedVersion => KeyValueClientErrorKind.ConnectionFailure,
            ProtocolErrorCode.DatabaseNotFound => KeyValueClientErrorKind.ConnectionFailure,
            ProtocolErrorCode.AuthenticationFailed => KeyValueClientErrorKind.AuthenticationFailure,
            ProtocolErrorCode.NotAuthorized => KeyValueClientErrorKind.NotAuthorized,
            ProtocolErrorCode.ParseFailure => KeyValueClientErrorKind.ParseFailure,
            ProtocolErrorCode.ExecutionFailure => KeyValueClientErrorKind.ExecutionFailure,
            ProtocolErrorCode.TransactionAborted => KeyValueClientErrorKind.TransactionAborted,
            ProtocolErrorCode.ProtocolViolation => KeyValueClientErrorKind.ProtocolViolation,
            ProtocolErrorCode.Unavailable => KeyValueClientErrorKind.Unavailable,
            _ => KeyValueClientErrorKind.Internal,
        };

        return new KeyValueClientException(kind, exception.Code, exception.Message, exception);
    }
}
