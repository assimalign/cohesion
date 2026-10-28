using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentRowWriter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentRowWriter.DebugView))]
internal sealed class DocumentRowWriter : IDisposable
{
    private readonly DocumentJsonBuffer _buffer = new();
    private readonly Utf8JsonWriter _writer;

    internal DocumentRowWriter() => _writer = new(_buffer, new JsonWriterOptions { Encoder = DocumentJsonEncoder.Instance, MaxDepth = 256 });

    private ReadOnlyMemory<byte> Value(object? value)
    {
        _buffer.Clear();
        _writer.Reset(_buffer);
        Write(_writer, value);
        _writer.Flush();
        return _buffer.WrittenMemory;
    }

    // A lone star is emitted directly; every other named projection has a row wrapper.
    internal static bool WritesBareValue(OqlSelectExpression query)
        => query.IsValue || query.Projections.Count == 1 && query.Projections[0].Expression is OqlStarExpression;

    internal ReadOnlyMemory<byte> Write(DatabaseRow row, IReadOnlyList<DatabaseColumn> columns, bool bareValue,
        Location? location = null, string resultClause = "SELECT")
    {
        try { return WriteRow(row, columns, bareValue); }
        catch (InvalidOperationException exception) when (_writer.CurrentDepth >= 256)
        {
            // Encoding is outside statement evaluation. Keep a terminal, located Failure
            // available even if a future producer bypasses projection validation.
            throw new DocumentPlanningException("CD05015", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ResultConstructorDepthExceeded, resultClause, "projection"), location, exception);
        }
    }

    private ReadOnlyMemory<byte> WriteRow(DatabaseRow row, IReadOnlyList<DatabaseColumn> columns, bool bareValue)
    {
        if (bareValue)
        {
            if (row is DocumentQueryResult.DocumentQueryRow { RawContent.IsEmpty: false } document) { return document.RawContent; }
            // VALUE/RAW roots have already normalized MISSING to NULL in the engine.
            return Value(row.GetValue(0));
        }
        return Projection(row, columns);
    }

    private ReadOnlyMemory<byte> Projection(DatabaseRow row, IReadOnlyList<DatabaseColumn> columns)
    {
        _buffer.Clear();
        _writer.Reset(_buffer);
        _writer.WriteStartObject();
        for (int index = 0; index < columns.Count; index++)
        {
            if (row.SupportsMissing && row.IsMissing(index)) { continue; }
            _writer.WritePropertyName(columns[index].Name);
            Write(_writer, row.GetValue(index));
        }
        _writer.WriteEndObject();
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentRowWriter value)
        {
            _ = value;
        }
    }
}
