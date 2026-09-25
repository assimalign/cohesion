using System;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Raised when a SQL row violates a named CHECK, FOREIGN KEY, or UNIQUE constraint.</summary>
/// <remarks>The rejected statement is rolled back. Raw unique keys and unrelated row values are omitted to avoid disclosing sensitive data.</remarks>
public sealed class SqlConstraintViolationException : DatabaseException
{
    /// <summary>Creates a constraint violation diagnostic.</summary>
    /// <param name="constraintName">The declared or generated constraint name.</param>
    /// <param name="table">The schema-qualified table whose constraint was violated.</param>
    /// <param name="constraintKind">The SQL constraint kind.</param>
    /// <param name="offendingValue">A safe scalar key value, or null when disclosure is inappropriate.</param>
    /// <param name="innerException">The underlying constraint error, when available.</param>
    public SqlConstraintViolationException(string constraintName, string table, string constraintKind, object? offendingValue = null, Exception? innerException = null)
        : base($"{constraintKind} constraint '{constraintName}' on '{table}' was violated{(constraintKind == "UNIQUE" ? " by a duplicate key" : string.Empty)}.", innerException)
    {
        ConstraintName = constraintName;
        Table = table;
        ConstraintKind = constraintKind;
        OffendingValue = offendingValue;
    }

    /// <summary>Gets the declared or generated constraint name.</summary>
    public string ConstraintName { get; }
    /// <summary>Gets the schema-qualified table name.</summary>
    public string Table { get; }
    /// <summary>Gets the SQL constraint kind.</summary>
    public string ConstraintKind { get; }
    /// <summary>Gets the offending value when safe to disclose; null otherwise.</summary>
    public object? OffendingValue { get; }
}
