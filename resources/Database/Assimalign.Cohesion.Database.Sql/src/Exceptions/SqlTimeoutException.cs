using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A server-side statement or lock timer stopped a statement. Its own changes are rolled back;
/// the session and an explicit transaction remain usable. Diagnostics contains CS05043
/// (statement timeout) or CS04044 (lock timeout).
/// </summary>
public sealed class SqlTimeoutException : DatabaseException
{
    private static readonly string _statementMessage = ErrorMessages.StatementTimeout;
    private static readonly string _lockMessage = ErrorMessages.LockTimeout;

    private SqlTimeoutException(string code, string sqlState, string message, Exception cause, Location? location)
        : base(message, [new Diagnostic
        {
            Code = code,
            Message = message,
            Severity = DiagnosticSeverity.Error,
            Start = location?.Start,
            End = location?.End,
            Line = location?.StartLine ?? 1,
            Location = location is null ? null : DiagnosticLocation.Absolute,
        }], cause)
    {
        SqlState = sqlState;
    }

    /// <summary>
    /// Gets the corresponding SQLSTATE: 57014 (query_canceled) for a statement timeout, or
    /// 55P03 (lock_not_available) for a lock timeout. The wire carries the engine diagnostic code.
    /// </summary>
    public string SqlState { get; }

    internal static SqlTimeoutException Statement(Exception cause, Location? location)
        => new("CS05043", "57014", _statementMessage, cause, location);

    internal static SqlTimeoutException Lock(Exception cause, Location? location)
        => new("CS04044", "55P03", _lockMessage, cause, location);
}
