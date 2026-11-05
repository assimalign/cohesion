using System;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents a single row within a database result set.
/// </summary>
/// <remarks>
/// Ordinal accessors validate bounds before dispatching to model-specific cores. Each model
/// retains its conversions and failures for a valid ordinal, including NULL and MISSING behavior.
/// IsMissing checks its optional capability before validating the ordinal.
/// </remarks>
public abstract class DatabaseRow
{
    private readonly bool _supportsMissing;

    /// <summary>Initializes a row with its optional MISSING capability.</summary>
    /// <param name="supportsMissing">Whether the row can distinguish MISSING from NULL.</param>
    protected DatabaseRow(bool supportsMissing = false)
    {
        _supportsMissing = supportsMissing;
    }

    /// <summary>Gets whether this row can distinguish MISSING from NULL.</summary>
    public bool SupportsMissing => _supportsMissing;

    /// <summary>Gets whether the field at the specified ordinal is MISSING.</summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>True for an absent value; false for NULL or a present value.</returns>
    /// <exception cref="NotSupportedException">MISSING is unavailable on this row.</exception>
    /// <exception cref="ArgumentOutOfRangeException">MISSING is supported and the ordinal is outside the row.</exception>
    public bool IsMissing(int ordinal)
    {
        if (!_supportsMissing) { throw new NotSupportedException(ErrorMessages.MissingNotSupported); }
        ValidateOrdinal(ordinal);
        return IsMissingCore(ordinal);
    }

    /// <summary>Reports MISSING for a row that supports the capability.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>True for an absent value; false for NULL or a present value.</returns>
    /// <exception cref="NotSupportedException">The row has not implemented this capability.</exception>
    protected virtual bool IsMissingCore(int ordinal) => throw new NotSupportedException(ErrorMessages.MissingNotSupported);

    /// <summary>
    /// Gets the number of fields in this row.
    /// </summary>
    public abstract int FieldCount { get; }

    /// <summary>
    /// Gets whether the field at the specified ordinal is null.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>True if the field is null; otherwise false.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public bool IsNull(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return IsNullCore(ordinal);
    }

    /// <summary>Reports whether the validated field is NULL.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>True when the field is NULL; false for a present or MISSING value.</returns>
    protected abstract bool IsNullCore(int ordinal);

    /// <summary>
    /// Gets the raw byte value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The raw bytes of the field value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public ReadOnlyMemory<byte> GetBytes(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetBytesCore(ordinal);
    }

    /// <summary>Reads the validated field as bytes.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field bytes, using the model's binary or text encoding and NULL/MISSING semantics.</returns>
    protected abstract ReadOnlyMemory<byte> GetBytesCore(int ordinal);

    /// <summary>
    /// Gets the string value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The string value, or null according to the model's NULL/MISSING semantics.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public string? GetString(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetStringCore(ordinal);
    }

    /// <summary>Reads the validated field as a string.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field string using model conversions, or null for NULL/MISSING.</returns>
    protected abstract string? GetStringCore(int ordinal);

    /// <summary>
    /// Gets the 32-bit integer value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The integer value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public int GetInt32(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetInt32Core(ordinal);
    }

    /// <summary>Reads the validated field as a 32-bit integer.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field converted to a 32-bit integer using model conversion and null rules.</returns>
    protected abstract int GetInt32Core(int ordinal);

    /// <summary>
    /// Gets the 64-bit integer value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The long value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public long GetInt64(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetInt64Core(ordinal);
    }

    /// <summary>Reads the validated field as a 64-bit integer.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field converted to a 64-bit integer using model conversion and null rules.</returns>
    protected abstract long GetInt64Core(int ordinal);

    /// <summary>
    /// Gets the boolean value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The boolean value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public bool GetBoolean(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetBooleanCore(ordinal);
    }

    /// <summary>Reads the validated field as Boolean.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field converted to Boolean using model conversion and null rules.</returns>
    protected abstract bool GetBooleanCore(int ordinal);

    /// <summary>
    /// Gets the double-precision floating point value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The double value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public double GetDouble(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetDoubleCore(ordinal);
    }

    /// <summary>Reads the validated field as a double-precision number.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field converted to double precision using model conversion and null rules.</returns>
    protected abstract double GetDoubleCore(int ordinal);

    /// <summary>
    /// Gets the value of the field at the specified ordinal as an object.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The boxed field value, or null if the field is NULL or MISSING. Use <see cref="IsMissing"/> on a supporting row to distinguish them.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public object? GetValue(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetValueCore(ordinal);
    }

    /// <summary>Reads the validated field as an object.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The existing boxed value or model output value, or null for NULL/MISSING.</returns>
    protected abstract object? GetValueCore(int ordinal);

    private void ValidateOrdinal(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, FieldCount);
    }
}
