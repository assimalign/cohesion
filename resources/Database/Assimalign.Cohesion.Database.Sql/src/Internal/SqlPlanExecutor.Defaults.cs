using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Decodes a stored version through the statement's bound table version: dropped
    /// columns' components are skipped, and only physically absent trailing fields are
    /// resolved from the bound column metadata. Stored NULLs and the original MVCC
    /// stamps are preserved.
    /// </summary>
    /// <param name="record">The stored record.</param>
    /// <param name="table">The table version the statement is bound to.</param>
    /// <param name="defaultValues">That version's bound DEFAULT values, by ordinal.</param>
    /// <param name="writer">The record's writer stamp.</param>
    /// <param name="deleter">The record's deleter stamp.</param>
    private static object?[]? DecodeRow(ReadOnlySpan<byte> record, SqlCatalogTable table, IReadOnlyList<string?> defaultValues,
        out TransactionSequence writer, out TransactionSequence deleter)
    {
        var values = SqlRowCodec.TryDecode(record, table, out writer, out deleter, out int storedColumnCount);
        if (values is not null)
        {
            for (int ordinal = storedColumnCount; ordinal < values.Length; ordinal++)
            {
                // A deleted historical version can predate a NOT NULL addition
                // to a currently empty table. Nullability validates live rows at
                // DDL/DML time, never changes the visibility of historical rows.
                values[ordinal] = defaultValues[ordinal] is string value ? ResolveDefault(table.Columns[ordinal], value) : null;
            }
        }
        return values;
    }

    /// <summary>
    /// Converts a column's DEFAULT value using the same rules for backfill and omitted
    /// INSERT values. Rejects string truncation, decimal rounding, nonfinite
    /// floats, and nonzero floating values that underflow to zero.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <param name="defaultValue">
    /// The value text of the column's DEFAULT literal, from the bound table version
    /// (<see cref="SqlBoundTable.DefaultValues"/>), or null when the column declares none.
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
