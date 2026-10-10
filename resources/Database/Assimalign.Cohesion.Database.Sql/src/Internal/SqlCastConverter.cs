using System;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Runs a bound <c>CAST</c> (phase E3): the cast of the engine's registry chosen for the value's
/// storage type (<see cref="SqlStandardCasts"/> and the application's), or the value unchanged when
/// it has the target's storage type already; then the target's length, precision and scale; then a
/// domain target's check. Loss of numeric precision or string content is an error.
/// </summary>
internal static class SqlCastConverter
{
    /// <summary>Converts a scalar, preserving null and rejecting a value no registered cast converts.</summary>
    /// <param name="value">The operand's value.</param>
    /// <param name="cast">The bound CAST.</param>
    /// <param name="cancellationToken">The statement's cancellation token, for a domain check's context.</param>
    /// <returns>The converted value; null for NULL.</returns>
    /// <exception cref="DatabaseException">
    /// No cast converts the value, the conversion failed or loses content, or the value violates the
    /// domain target's check; a <see cref="DatabaseException"/> a conversion threw passes unchanged.
    /// </exception>
    internal static object? Convert(object? value, SqlBoundCast cast, CancellationToken cancellationToken)
    {
        object? converted = null;
        if (value is not null)
        {
            try
            {
                var source = StorageTypeOf(value);
                SqlCast? registered = source == DatabaseType.Null ? null : cast.Casts[(int)source];
                if (registered is not null)
                {
                    converted = registered.Apply(value);
                }
                else if (source != DatabaseType.Null && source == cast.Target.Storage.Type)
                {
                    converted = value;
                }
                else
                {
                    throw new InvalidCastException(source == DatabaseType.Null
                        ? "Unsupported source/target pair."
                        : $"no cast from {SqlType.NameOf(source)} to {cast.Target.Name} is registered.");
                }

                if (converted is not null)
                {
                    converted = ApplyModifiers(converted, cast.Target.Storage);
                }
            }
            catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
            {
                throw new DatabaseException($"CAST from {value.GetType().Name} to {cast.TargetText} failed: {exception.Message}", exception);
            }
        }

        // A domain's check sees every value the CAST produces; a strict check passes NULL uncalled.
        if (cast.Target.Check is { } check && (converted is not null || check.NullBehavior == SqlNullBehavior.CalledOnNullInput)
            && check.InvokeResolved(converted, cast.CheckTarget, cast.Database, cast.Collation, cancellationToken) is false)
        {
            throw new DatabaseException(
                $"CAST to {cast.TargetText} failed: the value violates the check of domain '{cast.Target.Name}' ({check}).");
        }

        return converted;
    }

    /// <summary>
    /// Applies a target's length, precision and scale to a converted value, which never rounds or
    /// truncates: PostgreSQL's length coercion, a step after the cast itself.
    /// </summary>
    /// <param name="value">The converted value, of the target's storage type.</param>
    /// <param name="target">The target's storage type and constraints.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">A string or binary value is longer than the target, or a decimal has more scale.</exception>
    /// <exception cref="OverflowException">A decimal exceeds the target's precision.</exception>
    internal static object ApplyModifiers(object value, DatabaseTypeInfo target)
    {
        switch (value)
        {
            case string text when target.MaxLength is int length && text.Length > length:
                throw new InvalidCastException($"String length exceeds {length}; truncation is not supported.");
            case byte[] bytes when target.MaxLength is int length && bytes.Length > length:
                throw new InvalidCastException($"Binary length exceeds {length}; truncation is not supported.");
            case decimal number when target.Type == DatabaseType.Decimal:
                if (target.Scale is int scale && decimal.Round(number, scale) != number)
                {
                    throw new InvalidCastException($"Value exceeds scale {scale}; rounding is not supported.");
                }
                if (target.Precision is int precision)
                {
                    decimal limit = 1m;
                    for (int i = 0; i < precision - (target.Scale ?? 0); i++) { limit *= 10m; }
                    if (number <= -limit || number >= limit)
                    {
                        throw new OverflowException($"Value exceeds precision {precision} and scale {target.Scale ?? 0}.");
                    }
                }
                return number;
            default:
                return value;
        }
    }

    /// <summary>
    /// The storage type of a value as the row holds it: exactly one of the codec's CLR types, so a
    /// parameter of another CLR type (<see cref="byte"/>, <see cref="char"/>) has none and is refused,
    /// as it was before the cast registry.
    /// </summary>
    /// <param name="value">The value, not NULL.</param>
    /// <returns>The storage type; <see cref="DatabaseType.Null"/> for a CLR type the codec does not store.</returns>
    private static DatabaseType StorageTypeOf(object value) => value switch
    {
        string => DatabaseType.String,
        long => DatabaseType.Int64,
        int => DatabaseType.Int32,
        decimal => DatabaseType.Decimal,
        bool => DatabaseType.Boolean,
        short => DatabaseType.Int16,
        sbyte => DatabaseType.Int8,
        double => DatabaseType.Float64,
        float => DatabaseType.Float32,
        byte[] => DatabaseType.Binary,
        DateOnly => DatabaseType.Date,
        TimeOnly => DatabaseType.Time,
        DateTime => DatabaseType.DateTime,
        DateTimeOffset => DatabaseType.DateTimeOffset,
        TimeSpan => DatabaseType.TimeSpan,
        Guid => DatabaseType.Guid,
        _ => DatabaseType.Null,
    };

    /// <summary>
    /// Reads SQL numeric literals, including exponent notation, without rounding an
    /// oversized fractional mantissa before CAST can enforce its target constraints.
    /// </summary>
    internal static decimal ParseNumericLiteral(string text) => ParseExactDecimal(text, allowLiteralSyntax: true);

    /// <summary>Parses fixed-point text, optionally accepting SQL literal decimal-point and exponent forms.</summary>
    internal static decimal ParseExactDecimal(string text, bool allowLiteralSyntax = false)
    {
        string source = text.Trim();
        bool negative = source.StartsWith('-');
        if (source.StartsWith('+') || negative) { source = source[1..]; }
        int exponent = 0;
        int exponentStart = source.IndexOfAny(['e', 'E']);
        if (allowLiteralSyntax && exponentStart >= 0)
        {
            exponent = int.Parse(source.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            source = source[..exponentStart];
        }
        int point = source.IndexOf('.');
        if (source.Length == 0 || source == "." ||
            (!allowLiteralSyntax && (point == 0 || point == source.Length - 1)))
        {
            throw new FormatException("Expected signed fixed-point numeric text.");
        }
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] is < '0' or > '9' && i != point)
            {
                throw new FormatException("Expected signed fixed-point numeric text without grouping or exponent.");
            }
        }

        long scale = (point < 0 ? 0L : source.Length - point - 1L) - exponent;
        string digits = point < 0 ? source : source.Remove(point, 1);
        digits = digits.TrimStart('0');
        if (digits.Length == 0) { return 0m; }
        string significant = digits.TrimEnd('0');
        scale -= digits.Length - significant.Length;
        digits = significant;
        if (scale > 28 || digits.Length > 29 || digits.Length - scale > 29)
        {
            throw new OverflowException("Text cannot be represented exactly as System.Decimal.");
        }

        // Parse only the integer coefficient: this cannot silently round.
        decimal coefficient = decimal.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        while (scale < 0)
        {
            coefficient *= 10m;
            scale++;
        }
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(coefficient, bits);
        return new decimal(bits[0], bits[1], bits[2], negative, (byte)scale);
    }
}
