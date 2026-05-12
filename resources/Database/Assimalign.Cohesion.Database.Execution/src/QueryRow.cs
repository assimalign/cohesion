using System;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents a single row within a query result set.
/// </summary>
public abstract class QueryRow
{
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
    /// <returns>The boxed field value, or null if the field is null.</returns>
    public abstract object? GetValue(int ordinal);
}
