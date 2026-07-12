using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// One materialized row of a <see cref="SqlResultSet"/>, offering ordinal- and
/// name-based typed access to its column values.
/// </summary>
/// <remarks>
/// Values arrive already boxed to their runtime types by the shared value codec
/// (for example a <c>String</c> column yields <see cref="string"/>, an <c>Int32</c>
/// column yields <see cref="int"/>). Typed getters return the value directly when
/// its runtime type matches, and widen numeric values through
/// <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/> otherwise —
/// a mismatch that cannot be converted surfaces as a <see cref="SqlClientException"/>.
/// </remarks>
public sealed class SqlRow
{
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _ordinals;

    internal SqlRow(object?[] values, IReadOnlyDictionary<string, int> ordinals)
    {
        _values = values;
        _ordinals = ordinals;
    }

    /// <summary>
    /// Gets the number of columns in this row.
    /// </summary>
    public int FieldCount => _values.Length;

    /// <summary>
    /// Gets the raw boxed value at the given ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The boxed value, or null.</returns>
    public object? this[int ordinal] => GetValue(ordinal);

    /// <summary>
    /// Gets the raw boxed value for the named column.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The boxed value, or null.</returns>
    public object? this[string name] => GetValue(GetOrdinal(name));

    /// <summary>
    /// Resolves a column name to its ordinal.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The zero-based ordinal of the column.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or when no column carries it.</exception>
    public int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_ordinals.TryGetValue(name, out int ordinal))
        {
            throw new ArgumentException($"The result set has no column named '{name}'.", nameof(name));
        }

        return ordinal;
    }

    /// <summary>
    /// Gets the raw boxed value at the given ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The boxed value, or null.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ordinal"/> is out of range.</exception>
    public object? GetValue(int ordinal)
    {
        if ((uint)ordinal >= (uint)_values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        return _values[ordinal];
    }

    /// <summary>
    /// Indicates whether the value at the given ordinal is null.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>True when the value is null.</returns>
    public bool IsNull(int ordinal) => GetValue(ordinal) is null;

    /// <summary>
    /// Indicates whether the value for the named column is null.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>True when the value is null.</returns>
    public bool IsNull(string name) => GetValue(GetOrdinal(name)) is null;

    /// <summary>Gets the value at the ordinal as a <see cref="bool"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public bool GetBoolean(int ordinal) => Field<bool>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="short"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public short GetInt16(int ordinal) => Field<short>(ordinal);

    /// <summary>Gets the value at the ordinal as an <see cref="int"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public int GetInt32(int ordinal) => Field<int>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="long"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public long GetInt64(int ordinal) => Field<long>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="float"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public float GetSingle(int ordinal) => Field<float>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="double"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public double GetDouble(int ordinal) => Field<double>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="decimal"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public decimal GetDecimal(int ordinal) => Field<decimal>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="string"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public string GetString(int ordinal) => Field<string>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="Guid"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public Guid GetGuid(int ordinal) => Field<Guid>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="DateTime"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public DateTime GetDateTime(int ordinal) => Field<DateTime>(ordinal);

    /// <summary>Gets the value at the ordinal as a <see cref="byte"/> array.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public byte[] GetBytes(int ordinal) => Field<byte[]>(ordinal);

    /// <summary>Gets the value for the named column as a <see cref="bool"/>.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public bool GetBoolean(string name) => GetBoolean(GetOrdinal(name));

    /// <summary>Gets the value for the named column as an <see cref="int"/>.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public int GetInt32(string name) => GetInt32(GetOrdinal(name));

    /// <summary>Gets the value for the named column as a <see cref="long"/>.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public long GetInt64(string name) => GetInt64(GetOrdinal(name));

    /// <summary>Gets the value for the named column as a <see cref="double"/>.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public double GetDouble(string name) => GetDouble(GetOrdinal(name));

    /// <summary>Gets the value for the named column as a <see cref="decimal"/>.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public decimal GetDecimal(string name) => GetDecimal(GetOrdinal(name));

    /// <summary>Gets the value for the named column as a <see cref="string"/>.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public string GetString(string name) => GetString(GetOrdinal(name));

    /// <summary>Gets the value for the named column as a <see cref="Guid"/>.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as the requested type.</exception>
    public Guid GetGuid(string name) => GetGuid(GetOrdinal(name));

    /// <summary>
    /// Gets the value at the given ordinal as <typeparamref name="T"/>, widening
    /// numeric values where the conversion is lossless.
    /// </summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="SqlClientException">Thrown when the value is null or cannot be read as <typeparamref name="T"/>.</exception>
    public T GetFieldValue<T>(int ordinal) => Field<T>(ordinal);

    private T Field<T>(int ordinal)
    {
        object? value = GetValue(ordinal);

        if (value is null)
        {
            throw new SqlClientException(SqlClientErrorKind.InvalidCast, ProtocolErrorCode.Internal, $"The value at ordinal {ordinal} is null; use IsNull to guard nullable columns.");
        }

        if (value is T typed)
        {
            return typed;
        }

        try
        {
            // Numeric widening (Int32 column read as long, and so on) through the
            // IConvertible fast path — AOT-safe, no reflection.
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new SqlClientException(SqlClientErrorKind.InvalidCast, ProtocolErrorCode.Internal, $"The value at ordinal {ordinal} of runtime type {value.GetType()} cannot be read as {typeof(T)}.", exception);
        }
    }
}
