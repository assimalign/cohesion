using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Assimalign.Cohesion.Database.Graph.Language;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphExpressionEvaluator
{
    private static object? Function(GqlFunctionCallExpression call, IReadOnlyDictionary<string, object?> bindings,
        GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (!GraphScalarFunctions.ValidArity(call))
        { throw new GraphPlanningException("CG05001", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionArity, GraphScalarFunctions.Name(call.Function))); }
        GraphInlineArguments buffer = default;
        Span<object?> values = buffer;
        values = values[..call.Arguments.Count];
        for (int i = 0; i < values.Length; i++) { values[i] = Evaluate(call.Arguments[i], bindings, context, parameters); }
        var arguments = new GraphArguments(values, new GraphFunctionContext(context));
        try { return (context.Database?.Engine.Functions ?? GraphFunctionCatalog.Standard).Native(call.Function).Invoke(in arguments); }
        catch (GraphPlanningException error) { throw error.At(call.Location); }
    }

    internal static object? InvokeBuiltin(GqlScalarFunction function, ReadOnlySpan<object?> values, GraphEvaluationContext context)
    {
        if (function is GqlScalarFunction.CurrentDate or GqlScalarFunction.CurrentTimestamp)
        {
            return function == GqlScalarFunction.CurrentDate
                ? (object)DateOnly.FromDateTime(context.TransactionStart.UtcDateTime) : context.TransactionStart.ToUniversalTime();
        }
        for (int i = 0; i < values.Length; i++)
        {
            if ((GraphScalarFunctions.Argument(function, i) & GraphScalarFunctions.Kind(values[i])) == 0)
            { throw new GraphPlanningException("CG02013", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionKind, GraphScalarFunctions.Name(function), i + 1)); }
        }
        object? firstArgument = values.Length > 0 ? values[0] : null;
        object? second = values.Length > 1 ? values[1] : null;
        object? third = values.Length > 2 ? values[2] : null;
        object first = firstArgument!;
        try
        {
            return function switch
            {
                GqlScalarFunction.ElementId => first is GraphNode node ? node.Id.Value.ToString(CultureInfo.InvariantCulture) : ((GraphRelationship)first).Id.Value.ToString(CultureInfo.InvariantCulture),
                GqlScalarFunction.Labels => ((GraphNode)first).Labels.Order(StringComparer.Ordinal).Cast<object?>().ToArray(),
                GqlScalarFunction.Type => ((GraphRelationship)first).Type,
                GqlScalarFunction.Properties => new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(Properties(first), StringComparer.Ordinal)),
                GqlScalarFunction.Keys => Properties(first).Keys.Order(StringComparer.Ordinal).Cast<object?>().ToArray(),
                GqlScalarFunction.StartNode => context.Endpoint((GraphRelationship)first, true),
                GqlScalarFunction.EndNode => context.Endpoint((GraphRelationship)first, false),
                GqlScalarFunction.PathLength => (long)((GraphPath)first).Relationships.Count,
                GqlScalarFunction.Nodes => ((GraphPath)first).Nodes.Cast<object?>().ToArray(),
                GqlScalarFunction.Relationships => ((GraphPath)first).Relationships.Cast<object?>().ToArray(),
                GqlScalarFunction.Lower => ((string)first).ToLowerInvariant(),
                GqlScalarFunction.Upper => ((string)first).ToUpperInvariant(),
                GqlScalarFunction.Trim => ((string)first).Trim(' '),
                GqlScalarFunction.Substring => Substring((string)first, second!, third),
                GqlScalarFunction.Replace => Replace((string)first, (string)second!, (string)third!),
                GqlScalarFunction.CharLength => (long)((string)first).EnumerateRunes().Count(),
                GqlScalarFunction.Size => first is string text ? (long)text.EnumerateRunes().Count() : ((object?[])first).LongLength,
                GqlScalarFunction.Abs or GqlScalarFunction.Ceil or GqlScalarFunction.Floor or GqlScalarFunction.Round => Numeric(function, first, second is not null ? NonnegativeInteger(second) : 0),
                GqlScalarFunction.Sqrt => Finite(Math.Sqrt(Convert.ToDouble(first, CultureInfo.InvariantCulture))),
                GqlScalarFunction.Log => Logarithm(first, second),
                GqlScalarFunction.Mod => Arithmetic("%", first, second!),
                GqlScalarFunction.ToString => ScalarText(first, cypher: true),
                GqlScalarFunction.ToInteger => CypherInteger(first),
                GqlScalarFunction.ToFloat => first is string input ? CypherFloat(input) : Finite(Convert.ToDouble(first, CultureInfo.InvariantCulture)),
                GqlScalarFunction.ToBoolean => CypherBoolean(first),
                _ => throw new GraphPlanningException("CG05001", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionArity, GraphScalarFunctions.Name(function))),
            };
        }
        catch (Exception error) when (error is ArithmeticException or FormatException or InvalidCastException or ArgumentException)
        { throw new GraphPlanningException("CG02013", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionValue, GraphScalarFunctions.Name(function)), null, error); }
    }

    private static double Logarithm(object first, object? second)
    {
        double value = Convert.ToDouble(second ?? first, CultureInfo.InvariantCulture);
        if (value <= 0) { throw new ArithmeticException(ErrorMessages.NumericRequired); }
        if (second is null) { return Finite(Math.Log(value)); }
        double basis = Convert.ToDouble(first, CultureInfo.InvariantCulture);
        if (basis <= 0 || basis == 1) { throw new ArithmeticException(ErrorMessages.NumericRequired); }
        return Finite(Math.Log(value, basis));
    }

    private static IReadOnlyDictionary<string, object?> Properties(object value) => value switch
    {
        GraphNode node => node.Properties, GraphRelationship relationship => relationship.Properties,
        _ => (IReadOnlyDictionary<string, object?>)value,
    };

    private static long NonnegativeInteger(object value)
    {
        long result = WholeInteger(value);
        if (result < 0) { throw new ArithmeticException(ErrorMessages.NumericRequired); }
        return result;
    }

    private static int SubstringIndex(object value)
    {
        int index = checked((int)(long)CypherInteger(value)!);
        if (index < 0) { throw new ArithmeticException(ErrorMessages.NumericRequired); }
        return index;
    }

    // Neo4j StringWrappingStringValue.java:99-116 counts code points; Cypher starts at zero.
    private static string Substring(string text, object start, object? length)
    {
        long offset = SubstringIndex(start), count = length is null ? int.MaxValue : SubstringIndex(length);
        if (offset >= text.Length || count == 0) { return string.Empty; }
        int begin = text.Length, end = text.Length, index = 0;
        long position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (position == offset) { begin = index; }
            if (position >= offset && position - offset == count) { end = index; break; }
            index += rune.Utf16SequenceLength;
            position++;
        }
        return text[begin..end];
    }

    private static string Replace(string text, string search, string replacement)
    {
        if (search.Length == 0)
        {
            // Neo4j StringValue.java:197 delegates to Java String.replace: insert at
            // every UTF-16 boundary, including before and after the input.
            if (replacement.Length == 0) { return text; }
            long expanded = GraphCompositeEncoding.ScalarLength(text) + 5L + (text.Length + 1L) * (GraphCompositeEncoding.ScalarLength(replacement) - 4L);
            long characters = text.Length + (text.Length + 1L) * replacement.Length;
            if (expanded > GraphValueCodec.MaxPayloadLength || characters > GraphValueCodec.MaxPayloadLength)
            { throw new ArithmeticException(ErrorMessages.CompositeLimit); }
            var result = new StringBuilder((int)characters);
            result.Append(replacement);
            foreach (char character in text) { result.Append(character).Append(replacement); }
            return result.ToString();
        }
        long bytes = GraphCompositeEncoding.ScalarLength(text) + 5L;
        long growth = GraphCompositeEncoding.ScalarLength(replacement) - (long)GraphCompositeEncoding.ScalarLength(search);
        if (growth > 0)
        {
            int offset = 0;
            while ((offset = text.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
            {
                bytes += growth;
                if (bytes > GraphValueCodec.MaxPayloadLength) { throw new ArithmeticException(ErrorMessages.CompositeLimit); }
                offset += search.Length;
            }
        }
        return text.Replace(search, replacement, StringComparison.Ordinal);
    }

    private static object Numeric(GqlScalarFunction function, object value, long precision)
    {
        if (value is byte or ushort or uint or ulong) { value = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        return value switch
        {
            decimal number => function switch
            {
                GqlScalarFunction.Abs => Math.Abs(number), GqlScalarFunction.Ceil => Math.Ceiling(number),
                GqlScalarFunction.Floor => Math.Floor(number), _ => Math.Round(number, (int)Math.Min(precision, 28), MidpointRounding.AwayFromZero),
            },
            double number => Finite(function switch
            {
                GqlScalarFunction.Abs => Math.Abs(number), GqlScalarFunction.Ceil => Math.Ceiling(number),
                GqlScalarFunction.Floor => Math.Floor(number), _ => RoundApproximate(number, precision, number.ToString("R", CultureInfo.InvariantCulture)),
            }),
            float number => (float)Finite(function switch
            {
                GqlScalarFunction.Abs => MathF.Abs(number), GqlScalarFunction.Ceil => MathF.Ceiling(number),
                GqlScalarFunction.Floor => MathF.Floor(number), _ => RoundApproximate(number, precision, number.ToString("R", CultureInfo.InvariantCulture)),
            }),
            long number => function == GqlScalarFunction.Abs ? Math.Abs(number) : number,
            int number => function == GqlScalarFunction.Abs ? Math.Abs(number) : number,
            short number => function == GqlScalarFunction.Abs ? Math.Abs(number) : number,
            sbyte number => function == GqlScalarFunction.Abs ? checked((sbyte)Math.Abs((int)number)) : number,
            _ => throw new InvalidCastException(ErrorMessages.NumericRequired),
        };
    }

    // Round the finite value's round-trip decimal form as HALF_UP without Math.Round's
    // 15-place cutoff. Work is bounded by Double's representation, even for Int64 precision.
    private static double RoundApproximate(double number, long precision, string text)
    {
        if (number == 0 || precision > 324) { return number; }
        bool negative = text.StartsWith('-');
        if (negative) { text = text[1..]; }
        int exponentStart = text.IndexOfAny(['e', 'E']);
        int exponent = exponentStart < 0 ? 0 : int.Parse(text.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exponentStart >= 0) { text = text[..exponentStart]; }
        int point = text.IndexOf('.');
        int scale = (point < 0 ? 0 : text.Length - point - 1) - exponent;
        string digits = point < 0 ? text : text.Remove(point, 1);
        long keep = digits.Length - (long)scale + precision;
        if (keep >= digits.Length) { return number; }
        if (keep < 0) { return negative ? -0d : 0d; }
        bool carry = digits[(int)keep] >= '5';
        char[] rounded = digits[..(int)keep].ToCharArray();
        for (int i = rounded.Length - 1; carry && i >= 0; i--)
        { carry = rounded[i] == '9'; rounded[i] = carry ? '0' : (char)(rounded[i] + 1); }
        string coefficient = (carry ? "1" : "") + new string(rounded);
        if (coefficient.Length == 0) { return negative ? -0d : 0d; }
        return Finite(double.Parse((negative ? "-" : "") + coefficient + "e-" + precision.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    private static object? CypherFloat(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) { return Finite(number); }
        if (text.Contains('_'))
        {
            for (int i = 0; i < text.Length; i++)
            { if (text[i] == '_' && (i == 0 || i + 1 == text.Length || !char.IsAsciiDigit(text[i - 1]) || !char.IsAsciiDigit(text[i + 1]))) { return null; } }
            if (double.TryParse(text.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) { return Finite(number); }
        }
        object? integer = ParseCypherInteger(text);
        return integer is null ? null : Convert.ToDouble(integer, CultureInfo.InvariantCulture);
    }

    private static object? CypherInteger(object value)
    {
        if (value is bool boolean) { return boolean ? 1L : 0L; }
        if (value is string text)
        {
            return ParseCypherInteger(text);
        }
        if (value is float or double)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            // Java Number.longValue truncates and saturates finite floating values.
            if (number >= 9223372036854775808d) { return long.MaxValue; }
            if (number <= -9223372036854775808d) { return long.MinValue; }
            return (long)Math.Truncate(number);
        }
        return checked((long)decimal.Truncate(Convert.ToDecimal(value, CultureInfo.InvariantCulture)));
    }

    // Neo4j CypherRuntimeParser.java:164-195 uses exact decimal text before truncation.
    private static object? ParseCypherInteger(string text)
    {
        string source = text.Trim();
        bool negative = source.StartsWith('-');
        if (source.StartsWith('+') || negative) { source = source[1..]; }
        if (source.Length == 0) { return null; }
        // Cypher number-literal fallback supports hexadecimal/octal and digit separators.
        int radix = source.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : source.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : 10;
        if (radix != 10)
        {
            source = source[2..];
            if (source.Length == 0) { return null; }
            ulong integer = 0;
            bool digit = false;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '_' && digit && i + 1 < source.Length && source[i + 1] != '_') { digit = false; continue; }
                int number = source[i] is >= '0' and <= '9' ? source[i] - '0' : source[i] is >= 'a' and <= 'f' ? source[i] - 'a' + 10 : source[i] is >= 'A' and <= 'F' ? source[i] - 'A' + 10 : -1;
                if (number < 0 || number >= radix) { return null; }
                integer = checked(integer * (ulong)radix + (ulong)number); digit = true;
            }
            if (!digit) { return null; }
            return negative && integer == 1UL << 63 ? long.MinValue : negative ? checked(-(long)integer) : checked((long)integer);
        }
        if (source.Contains('_'))
        {
            for (int i = 0; i < source.Length; i++)
            { if (source[i] == '_' && (i == 0 || i + 1 == source.Length || !char.IsAsciiDigit(source[i - 1]) || !char.IsAsciiDigit(source[i + 1]))) { return null; } }
            source = source.Replace("_", "", StringComparison.Ordinal);
        }
        int exponent = 0, exponentStart = source.IndexOfAny(['e', 'E']);
        if (exponentStart >= 0)
        {
            if (!int.TryParse(source.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) { return null; }
            source = source[..exponentStart];
        }
        int point = source.IndexOf('.');
        if (source.Length == 0 || source == ".") { return null; }
        for (int i = 0; i < source.Length; i++)
        { if (i != point && !char.IsAsciiDigit(source[i])) { return null; } }
        long scale = (point < 0 ? 0L : source.Length - point - 1L) - exponent;
        string digits = (point < 0 ? source : source.Remove(point, 1)).TrimStart('0');
        if (digits.Length == 0) { return 0L; }
        long wholeDigits = digits.Length - scale;
        if (wholeDigits > 19) { throw new OverflowException(ErrorMessages.NumericRequired); }
        if (wholeDigits <= 0) { return 0L; }
        string whole = wholeDigits > digits.Length ? digits + new string('0', (int)(wholeDigits - digits.Length)) : digits[..(int)wholeDigits];
        long result = long.Parse((negative ? "-" : "") + whole, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if ((result == long.MaxValue || result == long.MinValue) && scale > 0 && digits.AsSpan((int)wholeDigits).ContainsAnyExcept('0'))
        { throw new OverflowException(ErrorMessages.NumericRequired); }
        return result;
    }

    private static object? CypherBoolean(object value) => value switch
    {
        bool boolean => boolean,
        string text => bool.TryParse(text.Trim(), out bool parsed) ? parsed : null,
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
        _ => throw new InvalidCastException(ErrorMessages.BooleanRequired),
    };

    // DoubleValue.java:61-62 uses Java's presentation thresholds: scientific below 1e-3
    // or from 1e7, at least two significant digits, and a fractional digit for whole floats.
    private static string CypherFloatingText(object value)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        string text = value is float single ? single.ToString("R", CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);
        bool negative = text.StartsWith('-');
        if (number == 0) { return negative ? "-0.0" : "0.0"; }
        if (negative) { text = text[1..]; }
        bool scientific = Math.Abs(number) < 1e-3 || Math.Abs(number) >= 1e7;
        int exponentStart = text.IndexOfAny(['e', 'E']);
        int exponent = exponentStart < 0 ? 0 : int.Parse(text.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exponentStart >= 0) { text = text[..exponentStart]; }
        int point = text.IndexOf('.');
        int position = (point < 0 ? text.Length : point) + exponent;
        string raw = point < 0 ? text : text.Remove(point, 1);
        string digits = raw.TrimStart('0');
        position -= raw.Length - digits.Length;
        digits = digits.TrimEnd('0');
        if (scientific && digits.Length == 1)
        {
            // Java asks for two significant digits where one digit already round-trips,
            // notably Double.MIN_VALUE (4.9E-324 rather than .NET's 5E-324).
            string detailed = value is float small ? MathF.Abs(small).ToString("E1", CultureInfo.InvariantCulture) : Math.Abs(number).ToString("E1", CultureInfo.InvariantCulture);
            int split = detailed.IndexOf('E');
            int detailedExponent = int.Parse(detailed.AsSpan(split + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return (negative ? "-" : "") + detailed[..split] + "E" + detailedExponent.ToString(CultureInfo.InvariantCulture);
        }
        string result = scientific
            ? digits[..1] + "." + digits[1..] + "E" + (position - 1).ToString(CultureInfo.InvariantCulture)
            : position <= 0 ? "0." + new string('0', -position) + digits
            : position >= digits.Length ? digits + new string('0', position - digits.Length) + ".0"
            : digits[..position] + "." + digits[position..];
        return (negative ? "-" : "") + result;
    }

    private static string ScalarText(object value, bool cypher) => value switch
    {
        string text => text,
        bool boolean => cypher ? boolean ? "true" : "false" : boolean ? "TRUE" : "FALSE",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => cypher
            ? timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture).Replace("+00:00", "Z", StringComparison.Ordinal)
            : timestamp.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        float or double when cypher => CypherFloatingText(value),
        _ when IsNumber(value) => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new InvalidCastException(ErrorMessages.CastValueInvalid),
    };
}
