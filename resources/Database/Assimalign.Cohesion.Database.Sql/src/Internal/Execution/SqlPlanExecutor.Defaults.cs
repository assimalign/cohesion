using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Decodes a stored version read from the table's page chain through the statement's bound
    /// table version: dropped columns' components are skipped, and only physically absent
    /// trailing fields are resolved from the bound column metadata. Stored NULLs and the
    /// original MVCC stamps are preserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record came from a slot the read found live on a data page of the table's own chain
    /// (<c>Storage.TryReadRecord</c> with the table's object id, or the owner-scoped unit
    /// iterator), so it is not another object's record and was not reclaimed, and one that does
    /// not decode is damaged. The statement fails with <see cref="StorageCorruptionException"/>,
    /// carrying the page id, instead of reading the row as absent (#1362), as PostgreSQL raises
    /// <c>ERRCODE_DATA_CORRUPTED</c> for a stored value inconsistent with its own header
    /// (<c>src/backend/access/heap/heaptoast.c:740-760</c>).
    /// </para>
    /// <para>
    /// Storage confirms the owned copy against every page mutation, including forward writes
    /// and rollback restores (#1365, #1372). A failed decode is therefore damage in a stable
    /// record, without speculative re-reads or a wait for the whole physical transaction.
    /// </para>
    /// </remarks>
    /// <param name="record">The stored record.</param>
    /// <param name="pageId">The page the record was read from.</param>
    /// <param name="slotIndex">The slot the record was read from.</param>
    /// <param name="table">The table version the statement is bound to.</param>
    /// <param name="defaults">That version's frozen missing values, by ordinal (<see cref="SqlBoundTable.MissingValues"/>).</param>
    /// <param name="writer">The record's writer stamp.</param>
    /// <param name="deleter">The record's deleter stamp.</param>
    /// <returns>The row's values.</returns>
    /// <exception cref="StorageCorruptionException">The confirmed record does not decode through the table's definition.</exception>
    private object?[] DecodeRow(ReadOnlyMemory<byte> record, PageId pageId, int slotIndex, SqlCatalogTable table,
        IReadOnlyList<SqlBoundExpression?> defaults, out TransactionSequence writer, out TransactionSequence deleter)
    {
        _metrics?.RecordRowRead();
        object?[] values;
        int storedColumnCount;

        try
        {
            values = SqlRowCodec.Decode(record.Span, table, out writer, out deleter, out storedColumnCount);
        }
        catch (DatabaseTypeException defect)
        {
            throw new StorageCorruptionException(pageId, slotIndex, string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.StoredRowDecodeFailed, table.Schema, table.Name, table.ObjectId, defect.Message), defect);
        }

        for (int ordinal = storedColumnCount; ordinal < values.Length; ordinal++)
        {
            // A deleted historical version can predate a NOT NULL addition
            // to a currently empty table. Nullability validates live rows at
            // DDL/DML time, never changes the visibility of historical rows.
            // The frozen ADD COLUMN literal never follows a later SET/DROP DEFAULT.
            // Reading a row never evaluates an insert-time expression default.
            values[ordinal] = defaults[ordinal] is { } bound ? DefaultValue(bound) : null;
        }

        RowDecoded?.Invoke(values);
        return values;
    }

    /// <summary>
    /// Binds a column's persisted DEFAULT once per table version: literals become converted
    /// constants; loaded expression defaults retain a column-free tree evaluated when a write
    /// assigns the default. DDL folds immutable declarations after validating storage bounds.
    /// </summary>
    /// <remarks>
    /// DDL proves every DEFAULT converts before it publishes one, so the conversion succeeds for any
    /// catalog the engine wrote. One that does not convert binds as a <see cref="SqlBoundFailure"/>
    /// that converts it again, and so fails, each time the default is used, as it did before the
    /// value was bound: the table still opens, and reads and writes that never need the default
    /// still succeed. Converted binary constants are copied when delivered into a row, so a
    /// missing stored tail cannot expose the cached value to a caller.
    /// </remarks>
    /// <param name="column">The column.</param>
    /// <param name="subject">The definition's name for diagnostics.</param>
    /// <param name="functions">The functions its recorded calls resolve against.</param>
    /// <param name="collation">The database's default collation.</param>
    /// <param name="cancellationToken">The binding operation's token.</param>
    /// <param name="declaring">Whether DDL is accepting and validating the definition.</param>
    /// <returns>The bound DEFAULT.</returns>
    internal static SqlBoundExpression BindDefaultExpression(SqlCatalogColumn column, string subject,
        SqlFunctionEnvironment functions, Collation collation, CancellationToken cancellationToken, bool declaring = false)
    {
        var expression = SqlPersistedExpression.Load(column.DefaultLiteral!, subject);
        if (SqlSequenceDefault.IsCandidate(expression))
        {
            if (column.DefaultFunctionCalls.Count != 1 || !column.DefaultFunctionCalls[0].StartsWith("S:", StringComparison.Ordinal) ||
                !ulong.TryParse(column.DefaultFunctionCalls[0].AsSpan(2), out ulong sequenceId))
            { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
            if (expression is not SqlNextValueExpression && (!SqlSequenceDefault.TryId(expression, out ulong encodedId) || encodedId != sequenceId))
            { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
            return new SqlBoundDefault(new SqlBoundSequence(expression is SqlNextValueExpression ? "NEXT VALUE FOR" : "NEXTVAL", [new SqlBoundConstant(checked((long)sequenceId))]), column, true);
        }
        if (expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Null })
        {
            return new SqlBoundConstant(null);
        }
        if (expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Date or SqlLiteralType.Time or SqlLiteralType.Timestamp
            or SqlLiteralType.TimestampWithTimeZone or SqlLiteralType.Interval or SqlLiteralType.Binary } typed)
        {
            return new SqlBoundConstant(ResolveDefault(column, typed.Value));
        }
        if (SqlPersistedExpression.IsDefaultLiteral(expression))
        {
            string value = SqlPersistedExpression.LoadDefaultValue(column.DefaultLiteral!, subject);
            if (declaring)
            {
                return new SqlBoundConstant(ResolveDefault(column, value));
            }
            return BindDefault(column, value);
        }
        var scope = new SqlExpressionEvaluator([], null, defaultCollation: collation, functions: functions, cancellationToken: cancellationToken, foldConstants: false);
        try
        {
            SqlDefinitionBinder.PinCalls(expression, scope, column.DefaultFunctionCalls);
            SqlPersistedExpression.Bind(expression, scope, declaring);
            if (SqlDefinitionBinder.HasUnavailableCall(expression, scope))
            {
                throw new SqlExecutionException(SqlExecutionException.DamagedDefinitionCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DefaultSignaturesUnresolved, string.Join(", ", column.DefaultFunctionCalls)));
            }
            // A missing native domain remains readable under the stored base type;
            // the bound domain checker owns its existing CS02010 write refusal.
            var target = (column.DomainName is { } name ? functions.Types.Find(name) : null) ?? SqlType.ForStorage(column.Type);
            // DEFAULT text stores the original expression, without implicit column
            // coercion. Rebind that assignment, including a domain's base modifiers,
            // rather than persisting an explicit CAST that can truncate characters.
            var casts = column.RecastDefault ? functions.Types.ResolveCasts(scope.SourceDomain(expression), target) : null;
            var source = scope.StaticTypeOf(expression);
            bool assignmentCast = casts?[(int)source] is { Context: not SqlCastContext.Explicit };
            if (declaring)
            {
                SqlPlanner.ValidateExpression(expression, scope);
                SqlPlanner.ValidateDefaultType(expression, scope, column.Type.Type, column.Name, assignmentCast);
            }
            // Only TYPE recasts defaults. Existing ordinary expression defaults keep
            // their established CoerceForColumn behavior, including application domains.
            SqlBoundExpression value = scope.Bind(expression);
            if (assignmentCast)
            {
                value = new SqlBoundCast(value, target.Name, SqlType.ForStorage(column.Type), casts!,
                    functions.Database, collation, sourceType: source, assignment: true);
            }
            var volatility = DefaultVolatility(expression, scope);
            var bound = new SqlBoundDefault(value, column, volatility != SqlFunctionVolatility.Immutable);
            // Immutable defaults are proved at definition time, even on an empty table.
            // Loading retains the tree; stable and volatile calls run when a value is assigned.
            return declaring && volatility == SqlFunctionVolatility.Immutable
                ? new SqlBoundConstant(DeclaredDefaultValue(bound, column, cancellationToken)) : bound;
        }
        catch (SqlEvaluationException failure) when (declaring)
        {
            throw failure.WithContext(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DefaultExpressionFailure, column.Name, "DEFAULT"));
        }
        catch (DatabaseException failure) when (!declaring && SqlDefinitionBinder.HasUnavailableCall(expression, scope))
        {
            return new SqlBoundDefault(new SqlBoundFailure(() => throw SqlEvaluationException.UnregisteredFunction(new SqlUnresolvedDefinition(subject, column.DefaultLiteral!, failure.Message))), column);
        }
        catch (DatabaseException failure) when (!declaring)
        {
            throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DefaultLoadFailed, subject, failure.Message, SqlPersistedExpression.DamagedCatalogHint),
                SqlExecutionException.DiagnosticsOf(failure, SqlExecutionException.DamagedDefinitionCode, discardLocations: true), failure);
        }
    }

    // Declaration validates assignment type and bounds, not a row's NOT NULL constraint.
    // PostgreSQL heap.c:3409 coerces defaults before any row receives the value.
    private static object? DeclaredDefaultValue(SqlBoundExpression bound, SqlCatalogColumn column,
        CancellationToken cancellationToken, DateTimeOffset? transactionStart = null)
    {
        object? value = DefaultValue(bound is SqlBoundDefault expression ? expression.Value : bound,
            cancellationToken, transactionStart);
        try
        {
            value = value is null ? null : CoerceForColumn(value, column);
            EnsureFiniteDefault(value);
            return value;
        }
        catch (SqlEvaluationException failure)
        {
            throw failure.WithContext(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.DefaultStorageFailure, column.Name, column.Type.Type));
        }
        catch (SqlExecutionException failure) when (failure.Diagnostics[0].Code is SqlExecutionException.InvalidDatetimeCode
            or SqlExecutionException.DatetimeOverflowCode or SqlExecutionException.IntervalOverflowCode)
        {
            throw failure.WithContext(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.DefaultStorageFailure, column.Name, column.Type.Type));
        }
        catch (DatabaseException failure)
        {
            throw new SqlPlanningException("CS05013", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ArgumentTypeMismatchSqlEvaluation, string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.DefaultStorageFailure, column.Name, column.Type.Type) + ". " + failure.Message), innerException: failure);
        }
    }

    internal static void EnsureFiniteDefault(object? value)
    {
        if (value is float single && !float.IsFinite(single) || value is double number && !double.IsFinite(number))
        {
            throw SqlEvaluationException.NumericValueOutOfRange(ErrorMessages.DefaultMustBeFinite);
        }
    }

    internal static SqlBoundExpression BindDefault(SqlCatalogColumn column, string defaultValue)
    {
        try
        {
            return new SqlBoundConstant(ResolveDefault(column, defaultValue));
        }
        catch (Exception exception) when (exception is not (InsufficientExecutionStackException or OutOfMemoryException))
        {
            return DefaultFailure(column, defaultValue);
        }
    }

    // Built only on the failure path, so the closure is not allocated for a DEFAULT that converts.
    private static SqlBoundFailure DefaultFailure(SqlCatalogColumn column, string defaultValue)
        => new(() => ResolveDefault(column, defaultValue));

    /// <summary>Evaluates a bound insert default or reads a frozen missing value, copying binary constants.</summary>
    /// <param name="bound">The bound default expression or missing-value literal.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    /// <param name="transactionStart">The transaction-start instant for datetime functions.</param>
    /// <returns>The value.</returns>
    private static object? DefaultValue(SqlBoundExpression bound, CancellationToken cancellationToken = default, DateTimeOffset? transactionStart = null)
    {
        try
        {
            return bound.Kind == SqlBoundExpressionKind.Constant
                ? SqlBinaryValue.CopyShared(((SqlBoundConstant)bound).Value)
                : SqlExpressionEvaluator.ForExecution(null, cancellationToken, transactionStart: transactionStart).Evaluate(bound, [], copySharedBinary: true);
        }
        catch (DatabaseException failure) { throw SqlExecutionException.FromPersisted(failure); }
    }

    internal static SqlFunctionVolatility DefaultVolatility(SqlExpression expression, SqlExpressionEvaluator scope)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var volatility = expression is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName)
            ? scope.ResolveFunction(call)!.Volatility : SqlFunctionVolatility.Immutable;
        foreach (var child in SqlPlanner.Children(expression))
        {
            var nested = DefaultVolatility(child, scope);
            if (nested < volatility) { volatility = nested; }
        }
        return volatility;
    }

    // Serialize the already coerced fast-default value, never the original expression.
    // The persisted literal is read by the same ceiling parser as every definition.
    internal static string FrozenDefaultLiteral(object? value, DatabaseType target) => SqlCharacter.Output(value) switch
    {
        null => "NULL",
        bool boolean => boolean ? "TRUE" : "FALSE",
        DateOnly date => "DATE '" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
        TimeOnly time => "TIME '" + time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
        DateTime timestamp => "TIMESTAMP '" + timestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
        DateTimeOffset instant => "TIMESTAMPTZ '" + instant.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture) + "'",
        TimeSpan interval => "INTERVAL '" + (interval.Ticks < 0 ? "-PT" : "PT") +
            (Math.Abs((decimal)interval.Ticks) / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture) + "S'",
        byte[] bytes when target == DatabaseType.JsonBinary => "'\\x" + Convert.ToHexString(bytes) + "'",
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        sbyte or byte or short or int or long or decimal or float or double => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => "'" + Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''", StringComparison.Ordinal) + "'",
    };

    /// <summary>
    /// Converts a column's DEFAULT value using the same rules for backfill and omitted
    /// INSERT values. Applies assignment bounds and rejects nonfinite floats and nonzero
    /// floating values that underflow to zero.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <param name="defaultValue">
    /// The value text of the column's DEFAULT literal, or null when the column declares none. A
    /// statement's write reads the value bound once per table version (<see cref="BindDefault"/>)
    /// and calls this only for a column with no DEFAULT; DDL and pre-I/O schema validation call it
    /// to prove a new DEFAULT converts and satisfies its column's effective bounds.
    /// </param>
    internal static object? ResolveDefault(SqlCatalogColumn column, string? defaultValue)
    {
        if (defaultValue is null)
        {
            if (!column.IsNullable)
            {
                throw SqlEvaluationException.NotNullViolation(column.Name, missingDefault: true);
            }
            return null;
        }

        try
        {
            SqlLiteralExpression? typed = column.DefaultLiteral is { } persisted
                ? SqlPersistedExpression.Load(persisted, $"DEFAULT of column '{column.Name}'") as SqlLiteralExpression : null;
            DatabaseType? literalType = typed?.LiteralType switch
            {
                SqlLiteralType.Date => DatabaseType.Date, SqlLiteralType.Time => DatabaseType.Time,
                SqlLiteralType.Timestamp => DatabaseType.DateTime, SqlLiteralType.TimestampWithTimeZone => DatabaseType.DateTimeOffset,
                SqlLiteralType.Interval => DatabaseType.TimeSpan, SqlLiteralType.Binary => DatabaseType.Binary, _ => null,
            };
            if (literalType is { } declared && declared != column.Type.Type)
            {
                throw new SqlExecutionException(SqlExecutionException.DatatypeMismatchCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DefaultLiteralTypeMismatch, typed!.LiteralType, column.Type.Type));
            }
            object? value = literalType is not null ? CoerceForColumn(SqlExpressionEvaluator.EvaluateLiteral(typed!), column)
                : column.Type.Type is DatabaseType.Binary or DatabaseType.JsonBinary
                ? CoerceForColumn(ParseBinaryDefault(defaultValue), column)
                : column.Type.Type == DatabaseType.Decimal
                // Canonical DEFAULT text preserves quotes. Only an unquoted numeric literal
                // accepts exponent/decimal-point forms; quoted text uses CAST's grammar.
                ? CoerceForColumn(SqlCastConverter.ParseNumericText(defaultValue, column.Type,
                    allowLiteralSyntax: column.DefaultLiteral is { } literal && !literal.StartsWith('\'')), column)
                : CoerceForColumn(defaultValue, column);
            EnsureFiniteDefault(value);
            if (value is float and 0f or double and 0d)
            {
                ReadOnlySpan<char> mantissa = defaultValue.AsSpan();
                int exponent = mantissa.IndexOfAny('e', 'E');
                if (exponent >= 0)
                {
                    mantissa = mantissa[..exponent];
                }
                if (mantissa.IndexOfAnyInRange('1', '9') >= 0)
                {
                    throw new SqlExecutionException(SqlEvaluationException.NumericValueOutOfRangeCode, ErrorMessages.DefaultUnderflows);
                }
            }
            return value;
        }
        catch (SqlEvaluationException exception)
        {
            throw exception.WithContext($"Column '{column.Name}': DEFAULT value cannot be stored as {column.Type.Type}");
        }
        catch (OverflowException exception) when (column.Type.Type == DatabaseType.Decimal)
        {
            throw SqlEvaluationException.NumericValueOutOfRange(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DefaultValueStorageFailed, column.Name, column.Type.Type, exception.Message), exception);
        }
        catch (Exception exception) when (exception is DatabaseException or FormatException or OverflowException or InvalidCastException or ArgumentOutOfRangeException)
        {
            throw new DatabaseException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DefaultValueStorageFailed, column.Name, column.Type.Type, exception.Message),
                exception is DatabaseException coded ? SqlExecutionException.DiagnosticsOf(coded) : new SqlExecutionException(
                    SqlExecutionException.ConversionCode(exception), exception.Message).Diagnostics, exception);
        }
    }

    private static byte[] ParseBinaryDefault(string value)
    {
        // PostgreSQL's bytea hex input uses this prefix (src/backend/utils/adt/bytea.c:201-224).
        // Defaults accept hex only: no octal escapes, whitespace or general text-to-binary cast.
        if (!value.StartsWith("\\x", StringComparison.Ordinal))
        {
            throw new FormatException(ErrorMessages.PlanExecutorParseBinaryDefaultFormat);
        }

        return Convert.FromHexString(value.AsSpan(2));
    }
}
