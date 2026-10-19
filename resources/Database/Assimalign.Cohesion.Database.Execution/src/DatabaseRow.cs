using System;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents a single row within a database result set.
/// </summary>
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
    /// <exception cref="NotSupportedException">This row does not support MISSING.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is outside the row.</exception>
    public bool IsMissing(int ordinal)
    {
        if (!_supportsMissing) { throw new NotSupportedException(ErrorMessages.MissingNotSupported); }
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, FieldCount);
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
    public abstract bool IsNull(int ordinal);

    /// <summary>
    /// Gets the raw byte value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The raw bytes of the field value.</returns>
    public abstract ReadOnlyMemory<byte> GetBytes(int ordinal);

    /// <summary>
    /// Gets the string value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The string value, or null if the field is null.</returns>
    public abstract string? GetString(int ordinal);

    /// <summary>
    /// Gets the 32-bit integer value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The integer value.</returns>
    public abstract int GetInt32(int ordinal);

    /// <summary>
    /// Gets the 64-bit integer value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The long value.</returns>
    public abstract long GetInt64(int ordinal);

    /// <summary>
    /// Gets the boolean value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The boolean value.</returns>
    public abstract bool GetBoolean(int ordinal);

    /// <summary>
    /// Gets the double-precision floating point value of the field at the specified ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The double value.</returns>
    public abstract double GetDouble(int ordinal);

    /// <summary>
    /// Gets the value of the field at the specified ordinal as an object.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The boxed field value, or null if the field is NULL or MISSING. Use <see cref="IsMissing"/> on a supporting row to distinguish them.</returns>
    public abstract object? GetValue(int ordinal);
}
