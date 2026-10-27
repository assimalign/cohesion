using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Raised when a SQL row violates a named CHECK, FOREIGN KEY, or UNIQUE constraint.</summary>
/// <remarks>
/// The rejected statement is rolled back. Diagnostics contains exactly one error: CS03037
/// (SQLSTATE 23505) for uniqueness, CS03038 (23514) for CHECK and domain checks,
/// CS03039 (23503) for foreign keys, or CS03024 (23502) for NOT NULL.
/// Raw unique keys and unrelated row values are omitted to avoid disclosing sensitive data.
/// </remarks>
public sealed class SqlConstraintViolationException : DatabaseException
{
    /// <summary>Creates a constraint violation diagnostic.</summary>
    /// <param name="constraintName">The declared or generated constraint name.</param>
    /// <param name="table">The schema-qualified table whose constraint was violated.</param>
    /// <param name="constraintKind">The SQL constraint kind.</param>
    /// <param name="offendingValue">A safe scalar key value, or null when disclosure is inappropriate.</param>
    /// <param name="innerException">The underlying constraint error, when available.</param>
    public SqlConstraintViolationException(string constraintName, string table, string constraintKind, object? offendingValue = null, Exception? innerException = null)
        : this(string.Format(CultureInfo.CurrentCulture,
            constraintKind == "UNIQUE" ? ErrorMessages.UniqueConstraintViolation : ErrorMessages.ConstraintViolation,
            constraintKind, constraintName, table), constraintName, table, constraintKind,
            offendingValue: offendingValue, innerException: innerException)
    {
    }

    /// <summary>
    /// Creates a violation whose message the engine words: a <c>CAST</c> to a domain whose check
    /// refuses the value, which names no table (phase E3).
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="constraintName">The constraint's name; a domain's name for a domain.</param>
    /// <param name="table">The schema-qualified table, or empty when no table is written.</param>
    /// <param name="constraintKind">The SQL constraint kind.</param>
    /// <param name="location">The statement's source span, when available.</param>
    /// <param name="offendingValue">The existing safe scalar value, when available.</param>
    /// <param name="innerException">The underlying constraint error, when available.</param>
    internal SqlConstraintViolationException(string message, string constraintName, string table, string constraintKind,
        Location? location = null, object? offendingValue = null, Exception? innerException = null)
        : base(message, [new Diagnostic
        {
            // A caller-supplied kind outside the engine's closed set uses the generic
            // CHECK classification rather than adding validation to the public constructor.
            Code = constraintKind switch
            {
                "UNIQUE" or "PRIMARY KEY" => "CS03037",
                "FOREIGN KEY" => "CS03039",
                "NOT NULL" => "CS03024",
                _ => "CS03038",
            },
            Message = message,
            Severity = DiagnosticSeverity.Error,
            Location = location is null ? null : DiagnosticLocation.Absolute,
            Start = location?.Start,
            End = location?.End,
            Line = location?.StartLine ?? 1,
        }], innerException)
    {
        ConstraintName = constraintName;
        Table = table;
        ConstraintKind = constraintKind;
        OffendingValue = offendingValue;
    }

    /// <summary>Attaches the executing statement's span when the failure has no location.</summary>
    /// <remarks>
    /// The located copy keeps the original violation as its inner exception (or that violation's own
    /// inner exception, when it wraps one), so the validator and executor frames are not lost.
    /// </remarks>
    internal SqlConstraintViolationException At(Location? location) => Diagnostics[0].Start is not null || location is null
        ? this : new(Message, ConstraintName, Table, ConstraintKind, location, OffendingValue, InnerException ?? this);

    /// <summary>Gets the declared or generated constraint name; a domain's name for kind <c>DOMAIN</c>.</summary>
    public string ConstraintName { get; }
    /// <summary>Gets the schema-qualified table name; empty for a <c>CAST</c> to a domain, which writes no table.</summary>
    public string Table { get; }
    /// <summary>Gets the SQL constraint kind.</summary>
    public string ConstraintKind { get; }
    /// <summary>Gets the offending value when safe to disclose; null otherwise.</summary>
    public object? OffendingValue { get; }
}
