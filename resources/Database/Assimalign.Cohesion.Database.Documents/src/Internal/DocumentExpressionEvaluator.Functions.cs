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
            "SQRT" => x < 0 ? throw Domain() : DecimalFromDouble(Math.Sqrt((double)x)),
            "POWER" => Power(x, y),
            _ => throw new DocumentPlanningException("COHDOC001", ErrorMessages.FunctionArgumentsInvalid),
        };
    }

    private object? StringFunction(string name, string text, object?[] values)
    {
        switch (name)
        {
            case "LOWER": case "UPPER":
                var changed = new StringBuilder(Math.Min(text.Length, maximumStringLength));
                int changedIndex = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    ObserveFunctionCancellation(changedIndex++);
                    Append(changed, (name == "LOWER" ? Rune.ToLowerInvariant(rune) : Rune.ToUpperInvariant(rune)).ToString());
                }
                return changed.ToString();
            case "LENGTH": return (long)CountFunctionRunes(text);
            case "SUBSTR":
                decimal startValue = Number(values[1]!);
                decimal? lengthValue = values.Length == 3 ? Number(values[2]!) : null;
                if (decimal.Truncate(startValue) != startValue || lengthValue is { } len && (len < 0 || decimal.Truncate(len) != len)) { return null; }
                decimal offset = startValue < 0 ? Math.Max(0, CountFunctionRunes(text) + startValue) : startValue;
                var substring = new StringBuilder();
                int substringIndex = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    ObserveFunctionCancellation(substringIndex++);
                    if (offset > 0) { offset--; continue; }
                    if (lengthValue <= 0) { break; }
                    Append(substring, rune.ToString());
                    lengthValue--;
                }
                return substring.ToString();
            case "POSITION":
                string pattern = (string)values[1]!;
                int position = FindFunctionText(text, pattern);
                return position < 0 ? -1L : (long)CountFunctionRunes(text.AsSpan(0, position));
            case "CONTAINS": return FindFunctionText(text, (string)values[1]!) >= 0;
            case "STARTS_WITH": return FunctionTextMatches(text, (string)values[1]!, false);
            case "ENDS_WITH": return FunctionTextMatches(text, (string)values[1]!, true);
            case "CONCAT":
                var concatenated = new StringBuilder();
                foreach (string piece in values.Cast<string>()) { _cancellationToken.ThrowIfCancellationRequested(); Append(concatenated, piece); }
                return concatenated.ToString();
            case "TRIM":
                var trim = new HashSet<Rune>();
                int trimIndex = 0, trimUnits = 0;
                foreach (var rune in (values.Length == 2 ? (string)values[1]! : " \t\n\f\r").EnumerateRunes())
                {
                    ObserveFunctionCancellation(trimIndex++);
                    if (trim.Add(rune)) { CheckStringLength(trimUnits += rune.Utf16SequenceLength); }
                }
                int start = 0, end = text.Length;
                while (start < end)
                {
                    ObserveFunctionCancellation(trimIndex++);
                    Rune.DecodeFromUtf16(text.AsSpan(start, end - start), out var rune, out int trimConsumed);
                    if (!trim.Contains(rune)) { break; }
                    start += trimConsumed;
                }
                while (end > start)
                {
                    ObserveFunctionCancellation(trimIndex++);
                    Rune.DecodeLastFromUtf16(text.AsSpan(start, end - start), out var rune, out int trimConsumed);
                    if (!trim.Contains(rune)) { break; }
                    end -= trimConsumed;
                }
                return JoinRunes(text.AsSpan(start, end - start));
            case "REPLACE":
                string old = (string)values[1]!, replacement = (string)values[2]!;
                decimal remaining = values.Length == 4 ? Number(values[3]!) : -1;
                if (remaining != decimal.Truncate(remaining))
                { throw new DocumentPlanningException("COHDOC008", ErrorMessages.ReplaceCountInvalid); }
                // Couchbase stringfun.html#replace: any negative count replaces all occurrences.
                bool replaceAll = remaining < 0;
                if (old.Length == 0 || remaining == 0) { return text; }
                var replaced = new StringBuilder();
                int consumed = 0;
                while (consumed < text.Length && (replaceAll || remaining > 0))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    int next = FindFunctionText(text, old, consumed);
                    if (next < 0) { break; }
                    Append(replaced, text.AsSpan(consumed, next - consumed));
                    Append(replaced, replacement);
                    consumed = next + old.Length;
                    if (!replaceAll) { remaining--; }
                }
                Append(replaced, text.AsSpan(consumed));
                return replaced.ToString();
            default: throw new DocumentPlanningException("COHDOC001", ErrorMessages.FunctionArgumentsInvalid);
        }
    }

    private int CountFunctionRunes(ReadOnlySpan<char> text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes()) { ObserveFunctionCancellation(count++); }
        return count;
    }

    private bool FunctionTextMatches(string text, string pattern, bool suffix)
    {
        if (pattern.Length > text.Length) { return false; }
        int start = suffix ? text.Length - pattern.Length : 0;
        for (int index = 0; index < pattern.Length; index += 1024)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(1024, pattern.Length - index);
            if (!text.AsSpan(start + index, length).SequenceEqual(pattern.AsSpan(index, length))) { return false; }
        }
        return true;
    }

    private int FindFunctionText(string text, string pattern, int start = 0)
    {
        if (pattern.Length == 0) { return start; }
        int end = text.Length - pattern.Length;
        if (start > end) { return -1; }
        if (pattern.Length == 1)
        {
            for (int index = start; index < text.Length;)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(1024, text.Length - index);
                int found = text.AsSpan(index, length).IndexOf(pattern[0]);
                if (found >= 0) { return index + found; }
                index += length;
            }
            return -1;
        }

        // PostgreSQL src/backend/utils/adt/varlena.c:1004-1070,1233-1271 uses
        // Boyer-Moore-Horspool with a fixed 256-entry skip table. Colliding UTF-16
        // units keep the shortest skip, preserving ordinal matches without an input-sized buffer.
        Span<int> skips = stackalloc int[256];
        skips.Fill(pattern.Length);
        for (int index = 0; index < pattern.Length - 1; index++)
        {
            ObserveFunctionCancellation(index);
            skips[pattern[index] & 255] = pattern.Length - 1 - index;
        }
        int iterations = 0;
        while (start <= end)
        {
            int index = pattern.Length - 1;
            while (index >= 0)
            {
                ObserveFunctionCancellation(unchecked(iterations++));
                if (text[start + index] != pattern[index]) { break; }
                index--;
            }
            if (index < 0) { return start; }
            int skip = skips[text[start + pattern.Length - 1] & 255];
            if (skip > end - start) { break; }
            start += skip;
        }
        return -1;
    }

    private string JoinRunes(ReadOnlySpan<char> text)
    {
        var builder = new StringBuilder();
        int index = 0;
        foreach (var rune in text.EnumerateRunes()) { ObserveFunctionCancellation(index++); Append(builder, rune.ToString()); }
        return builder.ToString();
    }

    private void Append(StringBuilder builder, ReadOnlySpan<char> text)
    {
        CheckStringLength((long)builder.Length + text.Length);
        for (int index = 0; index < text.Length; index += 1024)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            builder.Append(text.Slice(index, Math.Min(1024, text.Length - index)));
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
        if (exponent != decimal.Truncate(exponent)) { return DecimalFromDouble(Math.Pow((double)value, (double)exponent)); }
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

    private static decimal DecimalFromDouble(double value)
    {
        if (!double.IsFinite(value)) { throw new OverflowException(); }
        return decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
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
