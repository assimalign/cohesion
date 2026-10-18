using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    private const int maximumStringLength = 1_048_576;
    private static readonly string[] _dateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd HH:mm:ssK",
    ];

    private object? Call(OqlCallExpression call, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        var function = OqlFunctions.Find(call.Name);
        if (function is null || call.Name == "META" || !function.AcceptsArity(call.Arguments.Count))
        {
            throw new DocumentPlanningException("COHDOC001", ErrorMessages.FunctionArgumentsInvalid);
        }
        if (function.IsAggregate)
        {
            return Aggregate(call, group ?? throw new DocumentPlanningException("COHDOC001", ErrorMessages.AggregateContextMissing));
        }
        if (call.Name is "COALESCE" or "IFMISSING" or "IFNULL" or "IFMISSINGORNULL")
        {
            foreach (var argument in call.Arguments)
            {
                var value = Evaluate(argument, document, group);
                bool skip = call.Name switch
                {
                    "IFMISSING" => value is DocumentMissing,
                    "IFNULL" => value is null,
                    _ => value is null or DocumentMissing,
                };
                if (!skip) { return value; }
            }
            return null;
        }
        if (call.Name is "NOW_STR" or "NOW_MILLIS")
        {
            var instant = _transactionStart ?? throw new DocumentPlanningException("COHDOC001", ErrorMessages.FunctionClockMissing);
            return call.Name == "NOW_MILLIS" ? instant.ToUnixTimeMilliseconds() : DateString(instant);
        }
        var values = call.Arguments.Select(argument => Evaluate(argument, document, group)).ToArray();
        if (call.Name == "TYPE")
        {
            return values[0] switch
            {
                DocumentMissing => "missing", null => "null", bool => "boolean", decimal or long => "number",
                string => "string", JsonElement { ValueKind: JsonValueKind.Array } => "array", _ => "object",
            };
        }
        if (values.Any(value => value is DocumentMissing)) { return DocumentMissing.Instance; }
        if (values.Any(value => value is null)) { return null; }
        for (int i = 0; i < values.Length; i++)
        {
            if (!function.AcceptsArgument(i, FunctionKind(values[i]))) { return null; }
        }
        var first = values[0]!;
        if (call.Name.StartsWith("IS_", StringComparison.Ordinal))
        {
            return call.Name switch
            {
                "IS_ARRAY" => first is JsonElement { ValueKind: JsonValueKind.Array },
                "IS_OBJECT" => first is JsonElement { ValueKind: JsonValueKind.Object },
                "IS_STRING" => first is string,
                "IS_NUMBER" => first is decimal or long,
                _ => first is bool,
            };
        }
        if (call.Name is "ARRAY_LENGTH" or "ARRAY_CONTAINS")
        {
            if (first is not JsonElement { ValueKind: JsonValueKind.Array } array) { return null; }
            if (call.Name == "ARRAY_LENGTH") { return (long)array.GetArrayLength(); }
            int index = 0;
            foreach (var item in array.EnumerateArray())
            {
                ObserveFunctionCancellation(index++);
                if (Equal(Normalize(item), values[1]) is true) { return true; }
            }
            return false;
        }
        if (call.Name == "STR_TO_MILLIS")
        {
            return DateTimeOffset.TryParseExact((string)first, _dateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                ? date.ToUnixTimeMilliseconds() : null;
        }
        if (call.Name == "MILLIS_TO_STR")
        {
            decimal millis = Number(first);
            if (millis < -62135596800000m || millis > 253402300799999m) { return null; }
            return DateString(DateTimeOffset.FromUnixTimeMilliseconds((long)millis));
        }
        if (first is string text) { return StringFunction(call.Name, text, values); }
        decimal x = Number(first);
        decimal y = values.Length > 1 ? Number(values[1]!) : 0;
        return call.Name switch
        {
            "ABS" => decimal.Abs(x), "FLOOR" => decimal.Floor(x), "CEIL" => decimal.Ceiling(x),
            "ROUND" => Round(x, y, false), "TRUNC" => Round(x, y, true),
            "SQRT" => x < 0 ? throw Domain() : (decimal)Math.Sqrt((double)x),
            "POWER" => Power(x, y),
            _ => throw new DocumentPlanningException("COHDOC001", ErrorMessages.FunctionArgumentsInvalid),
        };
    }

    private object? StringFunction(string name, string text, object?[] values)
    {
        CheckStringLength(text.Length);
        switch (name)
        {
            case "LOWER": case "UPPER":
                var changed = new StringBuilder(text.Length);
                int changedIndex = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    ObserveFunctionCancellation(changedIndex++);
                    Append(changed, (name == "LOWER" ? Rune.ToLowerInvariant(rune) : Rune.ToUpperInvariant(rune)).ToString());
                }
                return changed.ToString();
            case "LENGTH": return (long)FunctionRunes(text).Length;
            case "SUBSTR":
                decimal startValue = Number(values[1]!);
                decimal? lengthValue = values.Length == 3 ? Number(values[2]!) : null;
                if (decimal.Truncate(startValue) != startValue || lengthValue is { } len && (len < 0 || decimal.Truncate(len) != len)) { return null; }
                var runes = FunctionRunes(text);
                decimal offset = startValue < 0 ? Math.Max(0, runes.Length + startValue) : Math.Min(startValue, runes.Length);
                int count = (int)Math.Min(lengthValue ?? runes.Length, runes.Length - offset);
                return JoinRunes(runes.AsSpan((int)offset, count));
            case "POSITION":
                string pattern = (string)values[1]!;
                CheckStringLength(pattern.Length);
                int position = text.IndexOf(pattern, StringComparison.Ordinal);
                return position < 0 ? -1L : (long)FunctionRunes(text[..position]).Length;
            case "CONTAINS": return text.Contains((string)values[1]!, StringComparison.Ordinal);
            case "STARTS_WITH": return text.StartsWith((string)values[1]!, StringComparison.Ordinal);
            case "ENDS_WITH": return text.EndsWith((string)values[1]!, StringComparison.Ordinal);
            case "CONCAT":
                var concatenated = new StringBuilder();
                foreach (string piece in values.Cast<string>()) { _cancellationToken.ThrowIfCancellationRequested(); Append(concatenated, piece); }
                return concatenated.ToString();
            case "TRIM":
                var source = FunctionRunes(text);
                var trim = FunctionRunes(values.Length == 2 ? (string)values[1]! : " ").ToHashSet();
                int start = 0, end = source.Length;
                while (start < end && trim.Contains(source[start])) { ObserveFunctionCancellation(start++); }
                while (end > start && trim.Contains(source[end - 1])) { ObserveFunctionCancellation(--end); }
                return JoinRunes(source.AsSpan(start, end - start));
            case "REPLACE":
                string old = (string)values[1]!, replacement = (string)values[2]!;
                CheckStringLength(old.Length);
                if (old.Length == 0) { return text; }
                var replaced = new StringBuilder();
                int consumed = 0;
                while (consumed < text.Length)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    int next = text.IndexOf(old, consumed, StringComparison.Ordinal);
                    if (next < 0) { Append(replaced, text[consumed..]); break; }
                    Append(replaced, text[consumed..next]);
                    Append(replaced, replacement);
                    consumed = next + old.Length;
                }
                return replaced.ToString();
            default: throw new DocumentPlanningException("COHDOC001", ErrorMessages.FunctionArgumentsInvalid);
        }
    }

    private Rune[] FunctionRunes(string text)
    {
        CheckStringLength(text.Length);
        var runes = new List<Rune>();
        foreach (var rune in text.EnumerateRunes()) { ObserveFunctionCancellation(runes.Count); runes.Add(rune); }
        return runes.ToArray();
    }

    private string JoinRunes(ReadOnlySpan<Rune> runes)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < runes.Length; i++) { ObserveFunctionCancellation(i); Append(builder, runes[i].ToString()); }
        return builder.ToString();
    }

    private void Append(StringBuilder builder, string text)
    {
        CheckStringLength((long)builder.Length + text.Length);
        for (int index = 0; index < text.Length; index += 1024)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            builder.Append(text.AsSpan(index, Math.Min(1024, text.Length - index)));
        }
    }

    private void ObserveFunctionCancellation(int index)
    {
        if ((index & 1023) == 0) { _cancellationToken.ThrowIfCancellationRequested(); }
    }

    private static void CheckStringLength(long length)
    {
        if (length > maximumStringLength) { throw new DocumentPlanningException("COHDOC007", ErrorMessages.FunctionStringTooLarge); }
    }

    private static decimal Round(decimal value, decimal scale, bool truncate)
    {
        if (scale != decimal.Truncate(scale)) { throw Domain(); }
        if (scale >= 28) { return value; }
        if (scale >= 0) { return decimal.Round(value, (int)scale, truncate ? MidpointRounding.ToZero : MidpointRounding.AwayFromZero); }
        // PostgreSQL src/backend/utils/adt/numeric.c:1548-1577 also bounds scale.
        if (scale < -28)
        {
            if (!truncate && scale == -29 && decimal.Abs(value) >= 50000000000000000000000000000m)
            { throw new OverflowException(); }
            return 0m;
        }
        decimal factor = 1;
        for (int index = 0; index < -(int)scale; index++) { factor *= 10; }
        // Dividing first can round 4.9999999999999999999999999999 / 10 to 0.5.
        // Preserve the exact remainder before deciding whether to round away from zero.
        decimal remainder = value % factor;
        decimal whole = decimal.Truncate(value - remainder);
        return !truncate && decimal.Abs(remainder) >= factor / 2 ? whole + decimal.Sign(value) * factor : whole;
    }

    private static decimal Power(decimal value, decimal exponent)
    {
        if (value == 0 && exponent < 0 || value < 0 && exponent != decimal.Truncate(exponent)) { throw Domain(); }
        if (exponent != decimal.Truncate(exponent)) { return (decimal)Math.Pow((double)value, (double)exponent); }
        if (exponent < 0) { value = 1 / value; exponent = -exponent; }
        decimal result = 1;
        while (exponent > 0)
        {
            if (exponent % 2 != 0) { result *= value; }
            exponent = decimal.Truncate(exponent / 2);
            if (exponent != 0) { value *= value; }
        }
        return result;
    }

    private static string DateString(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static JsonValueKind FunctionKind(object? value) => value switch
    {
        string => JsonValueKind.String, bool boolean => boolean ? JsonValueKind.True : JsonValueKind.False,
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double => JsonValueKind.Number,
        JsonElement json => json.ValueKind, null => JsonValueKind.Null, _ => JsonValueKind.Undefined,
    };

    private static DocumentPlanningException Domain() => new("COHDOC019", ErrorMessages.FunctionOutsideDomain);
}
