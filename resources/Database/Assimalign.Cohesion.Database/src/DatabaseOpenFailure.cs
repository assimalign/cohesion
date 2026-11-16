using System;

namespace Assimalign.Cohesion.Database;

/// <summary>Immutable safe metadata for the last failed open of a stored database.</summary>
public sealed class DatabaseOpenFailure
{
    internal DatabaseOpenFailure(DatabaseName name, string? failureCode, string exceptionType, DateTimeOffset failedAt, long attempts)
    {
        Name = name;
        FailureCode = failureCode;
        ExceptionType = exceptionType;
        FailedAt = failedAt;
        Attempts = attempts;
    }

    /// <summary>Gets the database name.</summary>
    public DatabaseName Name { get; }

    /// <summary>Gets the failure's diagnostic code, or null when it has none.</summary>
    public string? FailureCode { get; }

    /// <summary>Gets the exception's type name, without its message.</summary>
    public string ExceptionType { get; }

    /// <summary>Gets the UTC time of the last failed open.</summary>
    public DateTimeOffset FailedAt { get; }

    /// <summary>Gets the consecutive failed-open count.</summary>
    public long Attempts { get; }
}
