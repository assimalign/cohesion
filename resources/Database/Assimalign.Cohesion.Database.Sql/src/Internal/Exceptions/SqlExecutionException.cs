using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>An execution or DDL refusal with a stable diagnostic and an optional source span.</summary>
internal sealed class SqlExecutionException : DatabaseException
{
    private readonly Location? _location;
    internal const string CardinalityCode = "CS02046";
    internal const string InvalidDatetimeCode = "CS02047";
    internal const string DatetimeOverflowCode = "CS02048";
    internal const string DuplicateTableCode = "CS03049";
    internal const string DuplicateObjectCode = "CS03050";
    internal const string UndefinedTableCode = "CS01051";
    internal const string UndefinedObjectCode = "CS01052";
    internal const string InvalidTableCode = "CS03053";
    internal const string InvalidForeignKeyCode = "CS03054";
    internal const string DatatypeMismatchCode = "CS02055";
    internal const string DependentObjectsCode = "CS03056";
    internal const string UnsupportedCode = "CS05057";
    internal const string DamagedDefinitionCode = "CS06058";
    internal const string InvalidDefinitionCode = "CS03059";
    internal const string InternalErrorCode = "CS05060";
    internal const string DuplicateColumnCode = "CS03061";
    internal const string IntervalOverflowCode = "CS02062";
    internal const string CollationMismatchCode = "CS02063";
    internal const string SerializationFailureCode = "CS04064";
    internal const string DeadlockCode = "CS04065";
    internal const string DuplicateFunctionCode = "CS03065";
    internal const string InsufficientPrivilegeCode = "CS03066";
    internal const string StatementMemoryBudgetCode = "CS05067";
    internal const string DmlMultipleSourceMatchesCode = "CS05068";
    internal const string ReferentialAssignmentConflictCode = "CS05085";

    internal SqlExecutionException(string code, string message, Exception? innerException = null, Location? location = null)
        : base(message, [new Diagnostic
        {
            Code = code,
            Message = message,
            Severity = DiagnosticSeverity.Error,
            Start = location?.Start,
            End = location?.End,
            Line = location?.StartLine ?? 1,
            Location = location is null ? null : DiagnosticLocation.Absolute,
        }], innerException)
    {
        _location = location;
    }

    internal static SqlExecutionException ReferentialAssignmentConflict(string constraint, string schema, string table, string column)
        => new(ReferentialAssignmentConflictCode, string.Format(System.Globalization.CultureInfo.CurrentCulture,
            ErrorMessages.UpdateActionConflict, constraint, schema, table, column));

    internal SqlExecutionException At(Location? location) => Diagnostics[0].Start is not null || location is null
        ? this : new(Diagnostics[0].Code!, Message, InnerException ?? this, location);

    internal static DatabaseTransactionAbortedException SerializationFailure(TransactionAbortedException cause)
        => new(cause.Message, [new Diagnostic
        {
            Code = SerializationFailureCode,
            Message = cause.Message,
            Severity = DiagnosticSeverity.Error,
        }], cause);

    internal static DatabaseTransactionDeadlockException Deadlock(TransactionDeadlockException cause)
        => new(cause.Message, [new Diagnostic
        {
            Code = DeadlockCode,
            Message = cause.Message,
            Severity = DiagnosticSeverity.Error,
        }], cause);

    internal SqlExecutionException WithContext(string context)
        => new(Diagnostics[0].Code!, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ServerSessionExecuteAsyncValueTask, context, Message), this, _location);

    /// <summary>Preserves a wrapped carrier, coding an unclassified inner failure when necessary.</summary>
    internal static IReadOnlyList<Diagnostic> DiagnosticsOf(DatabaseException failure, string fallbackCode = InternalErrorCode,
        bool discardLocations = false)
    {
        if (failure.Diagnostics.Count == 0)
        {
            return [new Diagnostic { Code = fallbackCode, Message = failure.Message, Severity = DiagnosticSeverity.Error }];
        }
        if (!discardLocations) { return failure.Diagnostics; }
        var diagnostics = new Diagnostic[failure.Diagnostics.Count];
        for (int index = 0; index < diagnostics.Length; index++)
        {
            var diagnostic = failure.Diagnostics[index];
            diagnostics[index] = new Diagnostic { Code = diagnostic.Code, Message = diagnostic.Message, Severity = diagnostic.Severity };
        }
        return diagnostics;
    }

    // A persisted fragment's offsets belong to its synthetic parser carrier, even when
    // they happen to fit inside the caller's statement. Only the statement can locate it.
    internal static DatabaseException FromPersisted(DatabaseException failure)
    {
        if (SqlDatabase.IsStorageFailure(failure) || failure.Diagnostics is [{ Code: "CS04005" }, ..]) { return failure; }
        bool located = false;
        foreach (var diagnostic in failure.Diagnostics)
        {
            located |= diagnostic.Start is not null || diagnostic.End is not null || diagnostic.Location is not null || diagnostic.Line != 1;
        }
        if (!located) { return failure; }
        if (failure is SqlEvaluationException evaluation) { return evaluation.WithoutLocation(); }
        if (failure is SqlConstraintViolationException constraint)
        { return new SqlConstraintViolationException(constraint.Message, constraint.ConstraintName, constraint.Table,
            constraint.ConstraintKind, offendingValue: constraint.OffendingValue, innerException: constraint); }
        if (failure is SqlExecutionException execution)
        { return new SqlExecutionException(execution.Diagnostics[0].Code!, execution.Message, execution); }
        return new DatabaseException(failure.Message, DiagnosticsOf(failure, discardLocations: true), failure);
    }

    /// <summary>Classifies a failed store assignment without changing its existing message.</summary>
    internal static string ConversionCode(Exception failure) => failure switch
    {
        FormatException => SqlEvaluationException.InvalidTextRepresentationCode,
        OverflowException => SqlEvaluationException.NumericValueOutOfRangeCode,
        _ => DatatypeMismatchCode,
    };

}
