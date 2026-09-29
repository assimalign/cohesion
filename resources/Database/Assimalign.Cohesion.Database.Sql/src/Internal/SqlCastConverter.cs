using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Executes the exact scalar CAST subset against the shared type identity resolved
/// by the language parser. Loss of numeric precision or string content is an error.
/// </summary>
internal static class SqlCastConverter
{
    /// <summary>Converts a scalar, preserving null and rejecting unsupported source/target pairs.</summary>
    internal static object? Convert(object? value, SqlCastExpression cast)
    {
        var target = cast.TargetTypeInfo
            ?? throw new DatabaseException($"CAST target '{cast.TargetType}' has not been resolved.");

        if (value is null)
        {
            return null;
        }

        try
        {
            switch (target.Type)
            {
                case DatabaseType.String:
                    string text = value switch
                    {
                        string source => source,
                        bool flag => flag ? "TRUE" : "FALSE",
                        sbyte or short or int or long or decimal => System.Convert.ToString(value, CultureInfo.InvariantCulture)!,
                        _ => throw new InvalidCastException("Unsupported source/target pair."),
                    };
                    if (target.MaxLength is int length && text.Length > length)
                    {
                        throw new InvalidCastException($"String length exceeds {length}; truncation is not supported.");
                    }
                    return text;

                case DatabaseType.Boolean:
                    if (value is bool boolean) { return boolean; }
                    if (value is string booleanText && bool.TryParse(booleanText.Trim(), out bool parsed)) { return parsed; }
                    throw new InvalidCastException("BOOLEAN requires a boolean or TRUE/FALSE text.");

                case DatabaseType.Int8:
                case DatabaseType.Int16:
                case DatabaseType.Int32:
                case DatabaseType.Int64:
                case DatabaseType.Decimal:
                    decimal number = value switch
                    {
                        sbyte source => source,
                        short source => source,
                        int source => source,
                        long source => source,
                        decimal source => source,
                        string source => ParseExactDecimal(source),
                        _ => throw new InvalidCastException("Numeric targets require signed integers, decimal, or numeric text."),
                    };
                    if (target.Type == DatabaseType.Decimal)
                    {
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
                    }
                    if (decimal.Truncate(number) != number)
                    {
                        throw new InvalidCastException("An integer target requires a whole number; truncation is not supported.");
                    }
                    // Separate returns preserve the boxed CLR type, with checked range conversion.
                    switch (target.Type)
                    {
                        case DatabaseType.Int8: return checked((sbyte)number);
                        case DatabaseType.Int16: return checked((short)number);
                        case DatabaseType.Int32: return checked((int)number);
                        default: return checked((long)number);
                    }

                default:
                    throw new InvalidCastException("Unsupported target type.");
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw new DatabaseException($"CAST from {value.GetType().Name} to {cast.TargetType} failed: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Reads SQL numeric literals, including exponent notation, without rounding an
    /// oversized fractional mantissa before CAST can enforce its target constraints.
    /// </summary>
    internal static decimal ParseNumericLiteral(string text) => ParseExactDecimal(text, allowLiteralSyntax: true);

    /// <summary>Parses fixed-point text, optionally accepting SQL literal decimal-point and exponent forms.</summary>
    private static decimal ParseExactDecimal(string text, bool allowLiteralSyntax = false)
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
