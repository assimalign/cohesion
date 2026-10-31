using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Transient jsonb result spelling; never used for JSONB storage.</summary>
internal static class SqlJsonSerialization
{
    internal static void ValidateInput(object input, JsonElement root, SqlJsonPathExecution work)
    {
        // The shared reader has admitted syntax and Unicode. Inspect numeric token
        // spelling in the original input, without copying or expanding unselected
        // numbers. jsonb admission still validates overwritten duplicate members.
        ReadOnlySpan<char> text = input is string source ? source : [];
        ReadOnlySpan<byte> bytes = input is byte[] binary ? binary : [];
        bool quoted = false;
        int length = Math.Max(text.Length, bytes.Length);
        for (int index = 0; index < length; index++)
        {
            int character = Unit(text, bytes, index);
            if (quoted && character == '\\') { index++; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (quoted || character != '-' && character is not (>= '0' and <= '9')) { continue; }
            int first = index;
            while (index < length && Unit(text, bytes, index) is >= '0' and <= '9' or '.' or 'e' or 'E' or '+' or '-') { index++; }
            CheckNumber(text, bytes, first, index);
            index--;
        }
        Visit(root);

        void Visit(JsonElement item)
        {
            work.Visit();
            if (item.ValueKind == JsonValueKind.Array)
            { foreach (var child in item.EnumerateArray()) { Visit(child); } }
            else if (item.ValueKind == JsonValueKind.Object)
            { foreach (var property in item.EnumerateObject()) { work.Visit(); Visit(property.Value); } }
        }
    }

    private static int Unit(ReadOnlySpan<char> text, ReadOnlySpan<byte> bytes, int index)
        => text.IsEmpty ? bytes[index] : text[index];

    private static void CheckNumber(ReadOnlySpan<char> text, ReadOnlySpan<byte> bytes, int first, int end)
    {
        if (Unit(text, bytes, first) == '-') { first++; }
        int digits = 0, whole = 0, leadingZeros = 0, position = first;
        bool dot = false, nonzero = false;
        for (; position < end && Unit(text, bytes, position) is not ('e' or 'E'); position++)
        {
            int character = Unit(text, bytes, position);
            if (character == '.') { dot = true; continue; }
            digits++; if (!dot) { whole++; }
            if (!nonzero && character == '0') { leadingZeros++; } else { nonzero = true; }
        }
        long exponent = 0;
        if (position < end)
        {
            position++;
            bool negative = Unit(text, bytes, position) == '-';
            if (Unit(text, bytes, position) is '+' or '-') { position++; }
            for (; position < end; position++)
            {
                exponent = exponent * 10 + Unit(text, bytes, position) - '0';
                if (exponent > int.MaxValue / 2) { throw new SqlExecutionException("CS02084", ErrorMessages.JsonNumberOutOfRange); }
            }
            if (negative) { exponent = -exponent; }
        }
        long integral = whole + exponent;
        long scale = Math.Max(0, digits - integral);
        // numeric.c:238,261,6889-6945: exponent, packed scale and weight bounds.
        if (integral is > int.MaxValue or < int.MinValue || scale > 16383 || nonzero && integral - leadingZeros > 131072)
        { throw new SqlExecutionException("CS02084", ErrorMessages.JsonNumberOutOfRange); }
    }

    internal static IEnumerable<JsonProperty> Members(JsonElement item, SqlJsonPathExecution work)
    {
        var members = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            work.Visit();
            if (members.TryGetValue(property.Name, out var previous)) { Validate(previous.Value, work); }
            else { work.Reserve(160L + 2L * property.Name.Length); }
            members[property.Name] = property;
        }
        // jsonb orders object keys by UTF-8 length, then binary value.
        return members.Values.OrderBy(property => Encoding.UTF8.GetByteCount(property.Name))
            .ThenBy(property => property.Name, Utf8Comparer.Instance);
    }

    internal static void Validate(JsonElement item, SqlJsonPathExecution work)
    {
        // jsonb_in converts every value before uniqueifyJsonbObject drops earlier
        // duplicate members (jsonb.c:391-401, jsonb_util.c:2070-2125).
        work.Visit();
        if (item.ValueKind == JsonValueKind.Number) { Number(item.GetRawText(), work); }
        else if (item.ValueKind == JsonValueKind.Array)
        { foreach (var child in item.EnumerateArray()) { Validate(child, work); } }
        else if (item.ValueKind == JsonValueKind.Object)
        { foreach (var property in item.EnumerateObject()) { Validate(property.Value, work); } }
    }

    internal static string Render(IReadOnlyList<JsonElement> items, bool wrap, SqlJsonPathExecution work)
    {
        var text = new StringBuilder();
        if (wrap) { Append("["); }
        for (int index = 0; index < items.Count; index++)
        { if (index > 0) { Append(", "); } Write(items[index]); }
        if (wrap) { Append("]"); }
        work.Reserve(24L + 2L * text.Length);
        return text.ToString();

        void Append(string value) { work.Reserve(2L * value.Length + 16); text.Append(value); }
        void Write(JsonElement element)
        {
            work.Visit();
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Append("{"); bool first = true;
                    foreach (var property in Members(element, work))
                    {
                        if (!first) { Append(", "); } first = false;
                        WriteString(property.Name);
                        Append(": "); Write(property.Value);
                    }
                    Append("}"); break;
                case JsonValueKind.Array:
                    Append("["); bool firstItem = true;
                    foreach (var child in element.EnumerateArray())
                    { if (!firstItem) { Append(", "); } firstItem = false; Write(child); }
                    Append("]"); break;
                case JsonValueKind.String:
                    WriteString(element.GetString()!); break;
                case JsonValueKind.Number: Append(Number(element.GetRawText(), work)); break;
                default: Append(element.GetRawText()); break;
            }
        }
        void WriteString(string value)
        {
            // jsonb's escape_json emits Unicode directly and escapes JSON's ASCII
            // controls only (json.c:1532-1564). HTML encoders escape more characters.
            work.Reserve(2L * value.Length + 32);
            text.Append('"');
            foreach (char character in value)
            {
                string? escaped = character switch
                {
                    '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                    '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => null,
                };
                if (escaped is not null) { work.Reserve(2); text.Append(escaped); }
                else if (character < ' ')
                { work.Reserve(10); text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)); }
                else { text.Append(character); }
            }
            text.Append('"');
        }
    }

    internal static string Number(string raw, SqlJsonPathExecution work)
    {
        int exponentAt = raw.IndexOfAny(['e', 'E']);
        string mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        bool negative = mantissa[0] == '-';
        if (negative) { mantissa = mantissa[1..]; }
        int dot = mantissa.IndexOf('.');
        int whole = dot < 0 ? mantissa.Length : dot;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        if (exponentAt >= 0)
        {
            if (!int.TryParse(raw.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int exponent)
                || exponent is > int.MaxValue / 2 or < -(int.MaxValue / 2)
                || (long)whole + exponent is > int.MaxValue or < int.MinValue)
            { throw new SqlExecutionException("CS02084", ErrorMessages.JsonNumberOutOfRange); }
            whole += exponent;
        }
        int leadingZeros = 0;
        while (leadingZeros < digits.Length && digits[leadingZeros] == '0') { leadingZeros++; }
        long scale = Math.Max(0, digits.Length - (long)whole);
        // numeric.c:238,261 bounds the packed numeric scale and base-10000 weight.
        if (scale > 16383 || leadingZeros < digits.Length && (long)whole - leadingZeros > 131072)
        { throw new SqlExecutionException("CS02084", ErrorMessages.JsonNumberOutOfRange); }
        if (leadingZeros == digits.Length)
        { work.Reserve(8L + 2L * scale); return scale == 0 ? "0" : "0." + new string('0', (int)scale); }
        long length = (long)digits.Length + Math.Max(0, -whole) + Math.Max(0, whole - digits.Length) + 4;
        work.Reserve(length * 2);
        string number = whole <= 0 ? "0." + new string('0', -whole) + digits
            : whole >= digits.Length ? digits + new string('0', whole - digits.Length) : digits.Insert(whole, ".");
        int nonzero = 0;
        while (nonzero < number.Length - 1 && number[nonzero] == '0' && number[nonzero + 1] != '.') { nonzero++; }
        number = number[nonzero..];
        return negative && digits.AsSpan().ContainsAnyExcept('0') ? "-" + number : number;
    }

    private sealed class Utf8Comparer : IComparer<string>
    {
        internal static readonly Utf8Comparer Instance = new();
        public int Compare(string? x, string? y)
        {
            // UTF-8's scalar order differs from UTF-16 only across supplementary
            // characters; enumerate runes rather than allocating key byte arrays.
            var left = x!.EnumerateRunes(); var right = y!.EnumerateRunes();
            while (left.MoveNext() && right.MoveNext())
            { int order = left.Current.Value.CompareTo(right.Current.Value); if (order != 0) { return order; } }
            return x.Length.CompareTo(y.Length);
        }
    }
}
