using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Validated, statement-local JSON reading. No document or borrowed element escapes its owner.</summary>
internal static class SqlJsonAccess
{
    private static readonly UTF8Encoding _utf8 = new(false, true);
    private static readonly DatabaseTypeInfo _integer = new(DatabaseType.Decimal, scale: 0);
    // Invalid input is an ordinary FALSE result for IS JSON; cache our exception text.
    private static readonly string _invalidJsonText = ErrorMessages.CastJsonText;

    internal static bool IsOperator(SqlBinaryOperator op) => op is SqlBinaryOperator.JsonAccess or SqlBinaryOperator.JsonTextAccess;

    internal static DatabaseType ResultType(SqlBinaryOperator op, DatabaseType source)
        => op == SqlBinaryOperator.JsonTextAccess ? DatabaseType.String
            : source == DatabaseType.JsonBinary ? DatabaseType.JsonBinary : DatabaseType.Json;

    // Retain the existing CAST input depth and strict encoding. JSONB remains original UTF-8;
    // a reader owns only its transient DOM, never a normalized storage representation.
    internal static JsonDocument Read(object value)
    {
        if (value is not (string or byte[] or SqlCharacter)) { throw Signature(); }
        try
        {
            byte[] bytes;
            if (value is byte[] binary) { _ = _utf8.GetCharCount(binary); bytes = binary; }
            else { bytes = _utf8.GetBytes(value is SqlCharacter character ? character.Text : (string)value); }
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException or DecoderFallbackException)
        { throw new FormatException(_invalidJsonText, exception); }
    }

    internal static void Validate(object value)
    {
        using var document = ReadChecked(value);
    }

    internal static JsonDocument ReadChecked(object value)
    {
        try { return Read(value); }
        catch (FormatException exception) { throw new SqlExecutionException("CS02078", ErrorMessages.JsonInputInvalid, exception); }
    }

    internal static object? Extract(object value, object selector, DatabaseType source, bool asText)
    {
        // Unbound reader entry point for the oracle/allocation probes. Bound SQL
        // evaluation calls Select with the overload fixed on SqlBoundJsonStep.
        if (selector is not (string or SqlCharacter or short or int)) { throw Signature(); }
        return Extract(value, selector, source, asText, selector is short or int);
    }

    internal static object? Extract(object value, object selector, DatabaseType source, bool asText, bool isIndex)
    {
        using var document = ReadExtraction(value);
        var selected = Select(document.RootElement, selector, isIndex);
        return selected is null ? null : Serialize(selected.Value, source, asText);
    }

    internal static JsonDocument ReadExtraction(object value)
    {
        if (value is not (string or byte[])) { throw Signature(); }
        var document = ReadChecked(value);
        // PostgreSQL get_worker uses need_escapes=true even for a missing member
        // (jsonfuncs.c:1105-1116). Invalid surrogate escapes and \u0000 therefore
        // fail extraction wherever they occur. IS JSON only checks JSON syntax.
        try
        {
            if (value is string input ? input.Contains('\\') : ((byte[])value).AsSpan().Contains((byte)'\\'))
            {
                try { ValidateDecoded(document.RootElement); }
                catch (InvalidOperationException exception) { throw new SqlExecutionException("CS02078", ErrorMessages.JsonInputInvalid, exception); }
            }
            return document;
        }
        catch { document.Dispose(); throw; } // This method owns the DOM until it returns.
    }

    internal static JsonElement? Select(JsonElement container, object selector, bool isIndex)
    {
        JsonElement selected;
        if (!isIndex)
        {
            string name = selector is SqlCharacter character ? character.Text : (string)selector;
            // TryGetProperty follows PostgreSQL's last duplicate member wins rule.
            if (container.ValueKind != JsonValueKind.Object) { return null; }
            try
            {
                // Empty objects can skip lookup transcoding; validate their selectors too.
                _ = _utf8.GetByteCount(name);
                if (!container.TryGetProperty(name, out selected)) { return null; }
            }
            catch (ArgumentException exception) { throw new SqlExecutionException("CS02078", ErrorMessages.JsonInputInvalid, exception); }
        }
        else
        {
            if (container.ValueKind != JsonValueKind.Array) { return null; }
            int index = Convert.ToInt32(selector, CultureInfo.InvariantCulture);
            int count = container.GetArrayLength();
            // Addition cannot overflow, including int.MinValue; never negate the index.
            if (index < 0) { index += count; }
            if (index < 0 || index >= count) { return null; }
            selected = container[index];
        }

        return selected;
    }

    internal static object? Serialize(JsonElement selected, DatabaseType source, bool asText)
    {
        if (asText)
        {
            if (selected.ValueKind == JsonValueKind.Null) { return null; }
            try { return selected.ValueKind == JsonValueKind.String ? selected.GetString() : selected.GetRawText(); }
            catch (InvalidOperationException exception) { throw new SqlExecutionException("CS02078", ErrorMessages.JsonInputInvalid, exception); }
        }
        string raw = selected.GetRawText();
        return source == DatabaseType.JsonBinary ? _utf8.GetBytes(raw) : raw;
    }

    private static void ValidateDecoded(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) { Check(element.GetString()!); }
        else if (element.ValueKind == JsonValueKind.Array)
        { foreach (var item in element.EnumerateArray()) { ValidateDecoded(item); } }
        else if (element.ValueKind == JsonValueKind.Object)
        { foreach (var property in element.EnumerateObject()) { Check(property.Name); ValidateDecoded(property.Value); } }

        static void Check(string text)
        {
            if (text.Contains('\0')) { throw new SqlExecutionException("CS02078", ErrorMessages.JsonInputInvalid); }
        }
    }

    internal static bool IsJson(object value, SqlJsonKind kind)
    {
        try
        {
            using var document = Read(value);
            return kind switch
            {
                SqlJsonKind.Array => document.RootElement.ValueKind == JsonValueKind.Array,
                SqlJsonKind.Object => document.RootElement.ValueKind == JsonValueKind.Object,
                SqlJsonKind.Scalar => document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object),
                _ => true,
            };
        }
        catch (FormatException) { return false; }
    }

    // PostgreSQL jsonb.c:1819-2006: only a JSON number converts to a SQL number,
    // only a JSON Boolean to BOOLEAN, and JSON null becomes SQL NULL. The owner's
    // JSON extension uses the identical rule. Text CAST keeps PostgreSQL's JSON serialization.
    internal static object? Scalar(object value, DatabaseTypeInfo target)
    {
        using var document = ReadChecked(value);
        var scalar = document.RootElement;
        if (scalar.ValueKind == JsonValueKind.Null) { return null; }
        if (target.Type == DatabaseType.Boolean && scalar.ValueKind is JsonValueKind.True or JsonValueKind.False)
        { return scalar.GetBoolean(); }
        if (target.Type == DatabaseType.Boolean || scalar.ValueKind != JsonValueKind.Number)
        {
            throw new SqlExecutionException("CS02079", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.JsonScalarKindInvalid, scalar.ValueKind, SqlType.NameOf(target.Type)));
        }
        string number = scalar.GetRawText();
        try
        {
            // numeric_int2/int4/int8 round ties away from zero before checking range.
            if (target.Type == DatabaseType.Decimal) { return SqlCastConverter.ParseNumericText(number, target, allowLiteralSyntax: true); }
            // PostgreSQL converts through NUMERIC, whose zero has a positive sign
            // (numeric.c:4576-4611,4675-4715,11871-11875), even for JSON -0.
            if (target.Type == DatabaseType.Float32)
            { float real = SqlStandardCasts.ParseReal(number); return real == 0 ? 0f : real; }
            if (target.Type == DatabaseType.Float64)
            { double floating = SqlStandardCasts.ParseFloat(number); return floating == 0 ? 0d : floating; }
            decimal integer = SqlCastConverter.ParseNumericText(number, _integer, allowLiteralSyntax: true);
            if (target.Type == DatabaseType.Int8) { return checked((sbyte)integer); }
            if (target.Type == DatabaseType.Int16) { return checked((short)integer); }
            if (target.Type == DatabaseType.Int32) { return checked((int)integer); }
            return checked((long)integer);
        }
        catch (OverflowException exception) { throw SqlEvaluationException.NumericValueOutOfRange(exception.Message, exception); }
    }

    internal static SqlPlanningException Signature(Location? location = null)
        => new("CS01067", ErrorMessages.JsonSignatureInvalid, location);
}
