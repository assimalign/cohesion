using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // How many times DecodeConfirmed reads a slot again before it reports a record that does not
    // decode; it stops sooner when two reads agree.
    private const int confirmingReads = 2;

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
    /// The read holds a pin, not a latch (Storage DESIGN, "Reading a record through a
    /// reference"), so it can copy a slot while a writer reclaims it, such as the version purge
    /// freeing and clearing the page. That copy is torn, not damaged, so a failed decode is
    /// confirmed before it is reported (<see cref="DecodeConfirmed"/>). The confirmation narrows
    /// the window and does not close it: a writer descheduled half-way through rewriting the page
    /// leaves the same torn bytes for every read. A failed statement's bracket rollback is not
    /// such a writer since #1371: the storage read confirms itself against the page's restore
    /// (Storage DESIGN, "No reader sees a rollback's restore half done"). PostgreSQL needs no
    /// second read, because it reads a heap page under a share lock
    /// (<c>src/backend/access/heap/heapam.c:647</c>, <c>:1706</c>).
    /// </para>
    /// </remarks>
    /// <param name="record">The stored record.</param>
    /// <param name="pageId">The page the record was read from.</param>
    /// <param name="slotIndex">The slot the record was read from.</param>
    /// <param name="table">The table version the statement is bound to.</param>
    /// <param name="defaults">That version's frozen missing values, by ordinal (<see cref="SqlBoundTable.MissingValues"/>).</param>
    /// <param name="writer">The record's writer stamp.</param>
    /// <param name="deleter">The record's deleter stamp.</param>
    /// <returns>The row's values, or null when the slot was reclaimed before a confirming read.</returns>
    /// <exception cref="StorageCorruptionException">The record does not decode through the table's definition, and the confirming reads agree or ran out.</exception>
    private object?[]? DecodeRow(ReadOnlyMemory<byte> record, PageId pageId, int slotIndex, SqlCatalogTable table,
        IReadOnlyList<SqlBoundExpression?> defaults, out TransactionSequence writer, out TransactionSequence deleter)
    {
        object?[]? values;
        int storedColumnCount;

        try
        {
            values = SqlRowCodec.Decode(record.Span, table, out writer, out deleter, out storedColumnCount);
        }
        catch (DatabaseTypeException)
        {
            values = DecodeConfirmed(record, pageId, slotIndex, table, out writer, out deleter, out storedColumnCount);

            if (values is null)
            {
                return null;
            }
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

        return values;
    }

    /// <summary>
    /// Confirms a row record whose read did not decode (<see cref="DecodeRow"/>) by reading its
    /// slot again, with the table's owner check: a slot reclaimed by then is skipped like any
    /// reclaimed row, and a re-read that decodes is the row. A re-read that does not decode is
    /// corrupt when it returns the same bytes as the read before it, because damage is stable and
    /// a copy a writer tore is not; a re-read that differs means the slot is still changing, so
    /// the slot is read once more, up to <see cref="confirmingReads"/> re-reads.
    /// </summary>
    /// <param name="failed">The copy that did not decode.</param>
    /// <param name="pageId">The page the record was read from.</param>
    /// <param name="slotIndex">The slot the record was read from.</param>
    /// <param name="table">The table version the statement is bound to.</param>
    /// <param name="writer">The record's writer stamp.</param>
    /// <param name="deleter">The record's deleter stamp.</param>
    /// <param name="storedColumnCount">How many of the definition's live columns the record stores.</param>
    /// <returns>The live columns' values, or null when the slot was reclaimed.</returns>
    /// <exception cref="StorageCorruptionException">The record does not decode, and the reads agree or ran out.</exception>
    private object?[]? DecodeConfirmed(ReadOnlyMemory<byte> failed, PageId pageId, int slotIndex, SqlCatalogTable table,
        out TransactionSequence writer, out TransactionSequence deleter, out int storedColumnCount)
    {
        for (int read = 1; ; read++)
        {
            if (!_storage.TryReadRecord(pageId, slotIndex, table.ObjectId, out var record))
            {
                writer = default;
                deleter = default;
                storedColumnCount = 0;
                return null;
            }

            try
            {
                return SqlRowCodec.Decode(record.Span, table, out writer, out deleter, out storedColumnCount);
            }
            catch (DatabaseTypeException defect) when (read == confirmingReads || record.Span.SequenceEqual(failed.Span))
            {
                throw new StorageCorruptionException(
                    pageId,
                    $"The row record in slot {slotIndex} of page {(long)pageId} of table '{table.Schema}.{table.Name}' " +
                    $"(object {table.ObjectId}) does not decode: {defect.Message}");
            }
            catch (DatabaseTypeException)
            {
                failed = record;
            }
        }
    }

    /// <summary>
    /// Binds a column's persisted DEFAULT once per table version: the literal's value text
    /// converted to the column's type (<see cref="ResolveDefault"/>), so a decoded row that lacks
    /// the column and an INSERT that omits it read the converted value instead of converting the
    /// text again.
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
    /// <param name="defaultValue">The value text of the column's DEFAULT literal (<see cref="SqlPersistedExpression.LoadDefaultValue"/>).</param>
    /// <returns>The bound DEFAULT.</returns>
    internal static SqlBoundExpression BindDefaultExpression(SqlCatalogColumn column, string subject,
        SqlFunctionEnvironment functions, Collation collation, CancellationToken cancellationToken, bool declaring = false)
    {
        var expression = SqlPersistedExpression.Load(column.DefaultLiteral!, subject);
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
        var scope = new SqlExpressionEvaluator([], null, defaultCollation: collation, functions: functions, cancellationToken: cancellationToken);
        SqlDefinitionBinder.PinCalls(expression, scope, column.DefaultFunctionCalls);
        try
        {
            SqlPersistedExpression.Bind(expression, scope);
            if (SqlDefinitionBinder.HasUnavailableCall(expression, scope))
            {
                throw new DatabaseException($"The recorded function signatures {string.Join(", ", column.DefaultFunctionCalls)} do not resolve.");
            }
            if (declaring)
            {
                SqlPlanner.ValidateExpression(expression, scope);
                SqlPlanner.ValidateDefaultType(expression, scope, column.Type.Type, column.Name);
            }
            return new SqlBoundDefault(scope.Bind(expression), column);
        }
        catch (DatabaseException failure) when (!declaring && SqlDefinitionBinder.HasUnavailableCall(expression, scope))
        {
            return new SqlBoundDefault(new SqlBoundFailure(() => throw SqlEvaluationException.UnregisteredFunction(new SqlUnresolvedDefinition(subject, column.DefaultLiteral!, failure.Message))), column);
        }
        catch (DatabaseException failure) when (!declaring)
        {
            throw new DatabaseException($"{subject} cannot be loaded: {failure.Message} {SqlPersistedExpression.DamagedCatalogHint}", failure);
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

    /// <summary>The value a bound DEFAULT supplies: its converted constant, or its conversion failure raised again.</summary>
    /// <param name="bound">The bound DEFAULT (<see cref="BindDefault"/>).</param>
    /// <returns>The value.</returns>
    private static object? DefaultValue(SqlBoundExpression bound, CancellationToken cancellationToken = default)
    {
        object? value = bound.Kind == SqlBoundExpressionKind.Constant
            ? SqlBinaryValue.CopyShared(((SqlBoundConstant)bound).Value)
            : SqlExpressionEvaluator.ForExecution(null, cancellationToken).Evaluate(bound, [], copySharedBinary: true);
        return value;
    }

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
                throw new DatabaseException($"Typed DEFAULT {typed!.LiteralType} does not match column type {column.Type.Type}.");
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
            if (value is float single && !float.IsFinite(single) || value is double number && !double.IsFinite(number))
            {
                throw new DatabaseException("Floating-point defaults must be finite.");
            }
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
                    throw new DatabaseException("Nonzero floating-point default underflows to zero.");
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
                $"Column '{column.Name}': DEFAULT value cannot be stored as {column.Type.Type}. {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is DatabaseException or FormatException or OverflowException or InvalidCastException)
        {
            throw new DatabaseException(
                $"Column '{column.Name}': DEFAULT value cannot be stored as {column.Type.Type}. {exception.Message}", exception);
        }
    }

    private static byte[] ParseBinaryDefault(string value)
    {
        // PostgreSQL's bytea hex input uses this prefix (src/backend/utils/adt/bytea.c:201-224).
        // Defaults accept hex only: no octal escapes, whitespace or general text-to-binary cast.
        if (!value.StartsWith("\\x", StringComparison.Ordinal))
        {
            throw new FormatException("A binary DEFAULT must be a quoted \\x-prefixed hexadecimal value.");
        }

        return Convert.FromHexString(value.AsSpan(2));
    }
}
