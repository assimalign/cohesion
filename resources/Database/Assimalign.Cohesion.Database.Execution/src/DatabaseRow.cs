using System;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents a single row within a database result set.
/// </summary>
/// <remarks>
/// Ordinal accessors validate bounds before dispatching to model-specific cores. Each model
/// retains its conversions and failures for a valid ordinal, including NULL and MISSING behavior.
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
    /// <exception cref="NotSupportedException">The ordinal is valid but MISSING is unavailable on this row.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public bool IsMissing(int ordinal)
    {
        ValidateOrdinal(ordinal);
        if (!_supportsMissing) { throw new NotSupportedException(ErrorMessages.MissingNotSupported); }
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
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

    /// <summary>Reads the field after the base has validated its ordinal.</summary>
    /// <param name="ordinal">The validated zero-based column ordinal.</param>
    /// <returns>The field result with the model's conversion and null semantics.</returns>
    protected abstract object? GetValueCore(int ordinal);

    private void ValidateOrdinal(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, FieldCount);
    }
}
