using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>An execution or DDL refusal with a stable diagnostic and an optional source span.</summary>
internal sealed class SqlExecutionException : DatabaseException
{
    internal const string CardinalityCode = "COHSQLE046";
    internal const string InvalidDatetimeCode = "COHSQLE047";
    internal const string DatetimeOverflowCode = "COHSQLE048";
    internal const string DuplicateTableCode = "COHSQLE049";
    internal const string DuplicateObjectCode = "COHSQLE050";
    internal const string UndefinedTableCode = "COHSQLE051";
    internal const string UndefinedObjectCode = "COHSQLE052";
    internal const string InvalidTableCode = "COHSQLE053";
    internal const string InvalidForeignKeyCode = "COHSQLE054";
    internal const string DatatypeMismatchCode = "COHSQLE055";
    internal const string DependentObjectsCode = "COHSQLE056";
    internal const string UnsupportedCode = "COHSQLE057";
    internal const string DamagedDefinitionCode = "COHSQLE058";
    internal const string InvalidDefinitionCode = "COHSQLE059";
    internal const string InternalErrorCode = "COHSQLE060";
    internal const string DuplicateColumnCode = "COHSQLE061";

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
    }

    internal SqlExecutionException At(Location? location) => Diagnostics[0].Start is not null || location is null
        ? this : new(Diagnostics[0].Code!, Message, InnerException ?? this, location);

    /// <summary>Preserves a wrapped carrier, coding an unclassified inner failure when necessary.</summary>
    internal static IReadOnlyList<Diagnostic> DiagnosticsOf(DatabaseException failure, string fallbackCode = InternalErrorCode)
        => failure.Diagnostics.Count > 0 ? failure.Diagnostics : [new Diagnostic
        {
            Code = fallbackCode,
            Message = failure.Message,
            Severity = DiagnosticSeverity.Error,
        }];

    /// <summary>Classifies a failed store assignment without changing its existing message.</summary>
    internal static string ConversionCode(DatabaseType type, Exception failure) => failure switch
    {
        OverflowException or ArgumentOutOfRangeException when IsTemporal(type) => DatetimeOverflowCode,
        FormatException when IsTemporal(type) => InvalidDatetimeCode,
        FormatException => SqlEvaluationException.InvalidTextRepresentationCode,
        OverflowException => SqlEvaluationException.NumericValueOutOfRangeCode,
        _ => DatatypeMismatchCode,
    };

    private static bool IsTemporal(DatabaseType type) => type is DatabaseType.Date or DatabaseType.Time
        or DatabaseType.DateTime or DatabaseType.DateTimeOffset or DatabaseType.TimeSpan;
}
