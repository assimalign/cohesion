using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Canonical built-in CAST output and validated character input, shared with concatenation.</summary>
internal static class SqlCastText
{
    private static readonly UTF8Encoding _utf8 = new(false, true);

    // PostgreSQL float.c:578-589 uses shortest round-trip output; UUID and bytea use
    // uuid.c:108-145 and bytea.c:275-290 for their hyphenated and hex forms.
    internal static string Format(object value) => value switch
    {
        SqlCharacter character => character.Text,
        string text => text,
        bool boolean => boolean ? "TRUE" : "FALSE",
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset instant => instant.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        TimeSpan interval => FormatInterval(interval),
        Guid uuid => uuid.ToString("D"),
        byte[] bytes => "\\x" + Convert.ToHexStringLower(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string FormatInterval(TimeSpan value)
    {
        decimal ticks = decimal.Abs(value.Ticks);
        int days = (int)(ticks / TimeSpan.TicksPerDay);
        var time = new TimeOnly((long)(ticks % TimeSpan.TicksPerDay));
        string sign = value.Ticks < 0 ? "-" : string.Empty;
        string clock = sign + Format(time);
        if (days == 0) { return clock; }
        // PostgreSQL datetime.c:4675-4694,4875-4890: only positive one is singular;
        // omit the clock when the day field already expresses the whole interval.
        string unit = value.Ticks > 0 && days == 1 ? "day" : "days";
        string day = string.Create(CultureInfo.InvariantCulture, $"{sign}{days} {unit}");
        return time.Ticks == 0 ? day : day + " " + clock;
    }

    internal static string CharacterInput(object value) => value is SqlCharacter character ? character.Text : (string)value;

    internal static byte[] ParseBinary(string text)
    {
        string input = text.Trim();
        if (input.StartsWith("\\x", StringComparison.Ordinal)) { return SqlLiteralText.ParseBinary(input[2..]); }
        if (input.Contains('\\')) { throw new FormatException(ErrorMessages.CastBinaryText); }
        // PostgreSQL bytea.c:230-231 copies every unescaped byte, including whitespace.
        return Encode(text);
    }

    internal static string ValidateJson(string text)
    {
        ValidateJson(Encode(text));
        return text;
    }

    internal static byte[] JsonBytes(string text)
    {
        byte[] bytes = Encode(text);
        ValidateJson(bytes);
        return bytes;
    }

    internal static string JsonText(byte[] bytes)
    {
        ValidateJson(bytes);
        try { return _utf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new FormatException(ErrorMessages.CastUtf8Text, exception); }
    }

    private static byte[] Encode(string text)
    {
        try { return _utf8.GetBytes(text); }
        catch (EncoderFallbackException exception) { throw new FormatException(ErrorMessages.CastUtf8Text, exception); }
    }

    private static void ValidateJson(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException exception) { throw new FormatException(ErrorMessages.CastJsonText, exception); }
    }
}
