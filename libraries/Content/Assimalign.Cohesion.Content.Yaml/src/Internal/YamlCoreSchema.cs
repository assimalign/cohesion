using System;
using System.Globalization;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Tag resolution and value parsing for the YAML 1.2 core schema (specification section 10.2):
/// null, boolean, integer (decimal, octal, hexadecimal), and float forms. Applies to plain scalars
/// only — quoted and block scalars always resolve to strings.
/// </summary>
internal static class YamlCoreSchema
{
    internal static YamlScalarKind Resolve(string value)
    {
        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return YamlScalarKind.Null;
        }

        if (value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return YamlScalarKind.Boolean;
        }

        if (IsInteger(value))
        {
            return YamlScalarKind.Integer;
        }

        if (IsFloat(value))
        {
            return YamlScalarKind.Float;
        }

        return YamlScalarKind.String;
    }

    internal static long ParseInteger(string value)
    {
        if (value.StartsWith("0x", StringComparison.Ordinal))
        {
            return long.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        if (value.StartsWith("0o", StringComparison.Ordinal))
        {
            return Convert.ToInt64(value[2..], 8);
        }

        return long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    internal static double ParseFloat(string value) => value switch
    {
        ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" => double.PositiveInfinity,
        "-.inf" or "-.Inf" or "-.INF" => double.NegativeInfinity,
        ".nan" or ".NaN" or ".NAN" => double.NaN,
        _ => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
    };

    private static bool IsInteger(string value)
    {
        var span = value.AsSpan();
        if (span.StartsWith("0x"))
        {
            span = span[2..];
            if (span.Length == 0)
            {
                return false;
            }

            foreach (var character in span)
            {
                if (!char.IsAsciiHexDigit(character))
                {
                    return false;
                }
            }

            return true;
        }

        if (span.StartsWith("0o"))
        {
            span = span[2..];
            if (span.Length == 0)
            {
                return false;
            }

            foreach (var character in span)
            {
                if (character is < '0' or > '7')
                {
                    return false;
                }
            }

            return true;
        }

        if (span.Length > 0 && (span[0] == '-' || span[0] == '+'))
        {
            span = span[1..];
        }

        if (span.Length == 0)
        {
            return false;
        }

        foreach (var character in span)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFloat(string value)
    {
        if (value is ".inf" or ".Inf" or ".INF"
            or "+.inf" or "+.Inf" or "+.INF"
            or "-.inf" or "-.Inf" or "-.INF"
            or ".nan" or ".NaN" or ".NAN")
        {
            return true;
        }

        var span = value.AsSpan();
        if (span.Length > 0 && (span[0] == '-' || span[0] == '+'))
        {
            span = span[1..];
        }

        // [-+]? ( \. [0-9]+ | [0-9]+ ( \. [0-9]* )? ) ( [eE] [-+]? [0-9]+ )?
        var index = 0;
        var digitsBeforeDot = 0;
        while (index < span.Length && char.IsAsciiDigit(span[index]))
        {
            index++;
            digitsBeforeDot++;
        }

        var digitsAfterDot = 0;
        var hasDot = index < span.Length && span[index] == '.';
        if (hasDot)
        {
            index++;
            while (index < span.Length && char.IsAsciiDigit(span[index]))
            {
                index++;
                digitsAfterDot++;
            }
        }

        if (digitsBeforeDot == 0 && digitsAfterDot == 0)
        {
            return false;
        }

        // A plain integer pattern is not a float; without a dot an exponent is still a float.
        var hasExponent = index < span.Length && (span[index] is 'e' or 'E');
        if (hasExponent)
        {
            index++;
            if (index < span.Length && (span[index] is '-' or '+'))
            {
                index++;
            }

            var exponentDigits = 0;
            while (index < span.Length && char.IsAsciiDigit(span[index]))
            {
                index++;
                exponentDigits++;
            }

            if (exponentDigits == 0)
            {
                return false;
            }
        }

        return index == span.Length && (hasDot || hasExponent);
    }
}
