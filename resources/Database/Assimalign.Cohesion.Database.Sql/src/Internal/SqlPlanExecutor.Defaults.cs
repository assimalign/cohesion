using System;
using System.Collections.Generic;
using System.Globalization;

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
    /// reference"), so it can copy a slot while a writer reclaims it: a failed statement's
    /// bracket rollback restoring the page, or the version purge freeing and clearing it. That
    /// copy is torn, not damaged, so a failed decode is confirmed before it is reported
    /// (<see cref="DecodeConfirmed"/>). The confirmation narrows the window and does not close it:
    /// a writer descheduled half-way through rewriting the page leaves the same torn bytes for
    /// every read. PostgreSQL needs no second read, because it reads a heap page under a share
    /// lock (<c>src/backend/access/heap/heapam.c:647</c>, <c>:1706</c>).
    /// </para>
    /// </remarks>
    /// <param name="record">The stored record.</param>
    /// <param name="pageId">The page the record was read from.</param>
    /// <param name="slotIndex">The slot the record was read from.</param>
    /// <param name="table">The table version the statement is bound to.</param>
    /// <param name="defaults">That version's bound DEFAULTs, by ordinal (<see cref="SqlBoundTable.DefaultValues"/>).</param>
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
    /// still succeed. The converted value is immutable (a binary column has no literal DEFAULT that
    /// converts), so every row shares it.
    /// </remarks>
    /// <param name="column">The column.</param>
    /// <param name="defaultValue">The value text of the column's DEFAULT literal (<see cref="SqlPersistedExpression.LoadDefaultValue"/>).</param>
    /// <returns>The bound DEFAULT.</returns>
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
    private static object? DefaultValue(SqlBoundExpression bound)
        => bound.Kind == SqlBoundExpressionKind.Constant ? ((SqlBoundConstant)bound).Value : ((SqlBoundFailure)bound).Raise();

    /// <summary>
    /// Converts a column's DEFAULT value using the same rules for backfill and omitted
    /// INSERT values. Rejects string truncation, decimal rounding, nonfinite
    /// floats, and nonzero floating values that underflow to zero.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <param name="defaultValue">
    /// The value text of the column's DEFAULT literal, or null when the column declares none. A
    /// statement's write reads the value bound once per table version (<see cref="BindDefault"/>)
    /// and calls this only for a column with no DEFAULT; DDL calls it to prove a new DEFAULT converts.
    /// </param>
    private static object? ResolveDefault(SqlCatalogColumn column, string? defaultValue)
    {
        if (defaultValue is null)
        {
            if (!column.IsNullable)
            {
                throw new DatabaseException($"Column '{column.Name}' does not allow NULL and has no default.");
            }
            return null;
        }

        try
        {
            object? value = column.Type.Type == DatabaseType.Decimal
                ? SqlCastConverter.ParseNumericLiteral(defaultValue)
                : CoerceForColumn(defaultValue, column);
            if (value is string text && column.Type.MaxLength is int length && text.Length > length)
            {
                throw new DatabaseException($"String length exceeds {length}; truncation is not supported.");
            }
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
            if (value is decimal decimalValue)
            {
                int? scale = column.Type.Scale ?? (column.Type.Precision is null ? null : 0);
                if (scale is < 0 or > 28 || column.Type.Precision is < 1 || scale > column.Type.Precision)
                {
                    throw new DatabaseException("Invalid decimal precision or scale.");
                }
                if (scale is int digits && decimal.Round(decimalValue, digits) != decimalValue)
                {
                    throw new DatabaseException($"Value exceeds scale {digits}; rounding is not supported.");
                }
                if (column.Type.Precision is int precision)
                {
                    decimal integral = decimal.Truncate(decimalValue);
                    int integerDigits = integral == 0m ? 0 : integral.ToString("0", CultureInfo.InvariantCulture).TrimStart('-').Length;
                    if (integerDigits > precision - (scale ?? 0))
                    {
                        throw new DatabaseException($"Value exceeds precision {precision} and scale {scale ?? 0}.");
                    }
                }
            }
            return value;
        }
        catch (Exception exception) when (exception is DatabaseException or FormatException or OverflowException or InvalidCastException)
        {
            throw new DatabaseException(
                $"Column '{column.Name}': DEFAULT value cannot be stored as {column.Type.Type}. {exception.Message}", exception);
        }
    }
}
