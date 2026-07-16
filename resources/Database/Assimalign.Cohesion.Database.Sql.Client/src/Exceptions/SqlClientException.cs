using System;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// Thrown when a SQL client operation fails. Maps the shared client core's wire-coded
/// failures onto a SQL-scoped <see cref="SqlClientErrorKind"/> taxonomy while
/// preserving the underlying <see cref="ProtocolErrorCode"/>.
/// </summary>
public sealed class SqlClientException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="SqlClientException"/>.
    /// </summary>
    /// <param name="kind">The SQL-scoped failure category.</param>
    /// <param name="code">The underlying wire error code.</param>
    /// <param name="message">The error message.</param>
    public SqlClientException(SqlClientErrorKind kind, ProtocolErrorCode code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>
    /// Initializes a new <see cref="SqlClientException"/> with an inner exception.
    /// </summary>
    /// <param name="kind">The SQL-scoped failure category.</param>
    /// <param name="code">The underlying wire error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public SqlClientException(SqlClientErrorKind kind, ProtocolErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>
    /// Gets the SQL-scoped failure category.
    /// </summary>
    public SqlClientErrorKind Kind { get; }

    /// <summary>
    /// Gets the underlying wire error code.
    /// </summary>
    public ProtocolErrorCode Code { get; }

    /// <summary>
    /// Indicates whether the failure left the connection usable. Statement-level
    /// failures (parse, execution, transaction abort) keep the session ready;
    /// connection, protocol, and capacity failures break it.
    /// </summary>
    public bool ConnectionUsable => Kind is SqlClientErrorKind.ParseFailure
        or SqlClientErrorKind.ExecutionFailure
        or SqlClientErrorKind.TransactionAborted
        or SqlClientErrorKind.NotAuthorized
        or SqlClientErrorKind.InvalidCast;

    /// <summary>
    /// Translates a shared-core <see cref="DatabaseClientException"/> into a
    /// SQL-scoped exception, mapping its wire code onto a <see cref="SqlClientErrorKind"/>.
    /// </summary>
    /// <param name="exception">The core exception to translate.</param>
    /// <returns>The SQL-scoped exception.</returns>
    internal static SqlClientException FromClientException(DatabaseClientException exception)
    {
        SqlClientErrorKind kind = exception.Code switch
        {
            ProtocolErrorCode.UnsupportedVersion => SqlClientErrorKind.ConnectionFailure,
            ProtocolErrorCode.DatabaseNotFound => SqlClientErrorKind.ConnectionFailure,
            ProtocolErrorCode.AuthenticationFailed => SqlClientErrorKind.AuthenticationFailure,
            ProtocolErrorCode.NotAuthorized => SqlClientErrorKind.NotAuthorized,
            ProtocolErrorCode.ParseFailure => SqlClientErrorKind.ParseFailure,
            ProtocolErrorCode.ExecutionFailure => SqlClientErrorKind.ExecutionFailure,
            ProtocolErrorCode.TransactionAborted => SqlClientErrorKind.TransactionAborted,
            ProtocolErrorCode.ProtocolViolation => SqlClientErrorKind.ProtocolViolation,
            ProtocolErrorCode.Unavailable => SqlClientErrorKind.Unavailable,
            _ => SqlClientErrorKind.Internal,
        };

        return new SqlClientException(kind, exception.Code, exception.Message, exception);
    }
}
