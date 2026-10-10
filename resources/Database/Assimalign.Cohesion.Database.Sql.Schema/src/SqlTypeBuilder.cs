using System;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Configures a custom database type declared by <see cref="SqlSchemaBuilder.Type{T}"/>.
/// </summary>
/// <remarks>
/// <b>Shape (concrete-types plan, phase 4, §6.7).</b> A public sealed class with an internal
/// constructor; it replaced the <c>ISqlTypeBuilder</c> interface and its internal implementation.
/// The <c>Sdk.Database</c> extractor recognizes its calls by its metadata name.
/// </remarks>
public sealed class SqlTypeBuilder
{
    private readonly Type _clrType;
    private int? _precision;
    private int? _scale;

    /// <summary>
    /// Initializes a new builder for one custom type.
    /// </summary>
    /// <param name="clrType">The CLR type whose SQL type mapping the builder configures.</param>
    internal SqlTypeBuilder(Type clrType)
    {
        _clrType = clrType;
    }

    /// <summary>Represents the type as a fixed-precision decimal.</summary>
    /// <param name="precision">The total number of digits.</param>
    /// <param name="scale">The number of fractional digits.</param>
    /// <exception cref="ArgumentOutOfRangeException">The precision is less than one, the scale is negative, or the scale exceeds the precision.</exception>
    public void Decimal(int precision, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);

        if (scale > precision)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Decimal scale cannot exceed precision.");
        }

        _precision = precision;
        _scale = scale;
    }

    /// <summary>
    /// Snapshots the configured representation into the immutable declaration.
    /// </summary>
    /// <returns>The type declaration.</returns>
    internal SqlSchemaType Build()
        => new(_clrType, _precision, _scale);
}
