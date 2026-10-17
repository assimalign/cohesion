using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;


namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentJsonValues : IDisposable
{
    private readonly DocumentJsonBuffer _buffer = new();
    private readonly Utf8JsonWriter _writer;

    internal DocumentJsonValues() => _writer = new(_buffer, new JsonWriterOptions { MaxDepth = 256 });

    internal ReadOnlyMemory<byte> Result(IReadOnlyList<object?> values, bool wholeDocument)
    {
        _buffer.Clear();
        _writer.Reset(_buffer);
        if (!wholeDocument) { _writer.WriteStartArray(); }
        foreach (object? value in values) { Write(_writer, value); }
        if (!wholeDocument) { _writer.WriteEndArray(); }
        _writer.Flush();
        return _buffer.WrittenMemory;
    }

    public void Dispose() => _writer.Dispose();

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case string text: writer.WriteStringValue(text); break;
            case JsonElement json when json.ValueKind != JsonValueKind.Undefined:
                json.WriteTo(writer); break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture)); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(Convert.ToDecimal(number)); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(Convert.ToDecimal(number)); break;
            default: throw new ArgumentException(ErrorMessages.JsonScalarInvalid, nameof(value));
        }
    }
}
