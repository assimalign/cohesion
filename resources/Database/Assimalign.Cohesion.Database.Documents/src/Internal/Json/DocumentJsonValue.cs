using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentJsonValue
{
    internal static byte[] Encode(object? value, string? writeVerb = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = DocumentJsonEncoder.Instance, MaxDepth = 256 }))
        {
            Write(writer, value, writeVerb);
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static JsonElement Object(IEnumerable<KeyValuePair<string, object?>> members, string? writeVerb = null, int maxDepth = 256, string resultClause = "SELECT")
    {
        var keyed = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in members) { AddMember(keyed, member); }
        return Object(keyed, writeVerb, maxDepth, resultClause);
    }

    internal static async ValueTask<JsonElement> ObjectAsync(IAsyncEnumerable<KeyValuePair<string, object?>> members,
        string? writeVerb, int maxDepth, string resultClause)
    {
        var keyed = new Dictionary<string, object?>(StringComparer.Ordinal);
        await foreach (var member in members.ConfigureAwait(false)) { AddMember(keyed, member); }
        return Object(keyed, writeVerb, maxDepth, resultClause);
    }

    private static void AddMember(Dictionary<string, object?> keyed, KeyValuePair<string, object?> member)
    {
        // Couchbase expression/cons_object.go:53-77: last non-MISSING assignment wins.
        // Object serialization must follow this map stage on both paths, including
        // its failure order for values later replaced by another assignment.
        if (member.Value is not DocumentMissing) { keyed[member.Key] = member.Value; }
    }

    private static JsonElement Object(Dictionary<string, object?> keyed, string? writeVerb, int maxDepth, string resultClause)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = ConstructorWriter(buffer, writeVerb, maxDepth))
            {
                writer.WriteStartObject();
                foreach (var member in keyed)
                {
                    writer.WritePropertyName(member.Key);
                    Write(writer, member.Value, writeVerb);
                }
                writer.WriteEndObject();
            }
            return ReadConstructor(buffer.WrittenMemory, writeVerb, maxDepth);
        }
        catch (InvalidOperationException exception)
        {
            throw Depth("object", writeVerb, resultClause, exception);
        }
    }

    internal static JsonElement Array(IEnumerable<object?> elements, string? writeVerb = null, int maxDepth = 256, string resultClause = "SELECT")
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = ConstructorWriter(buffer, writeVerb, maxDepth))
            {
                writer.WriteStartArray();
                foreach (var element in elements) { WriteElement(writer, element, writeVerb); }
                writer.WriteEndArray();
            }
            return ReadConstructor(buffer.WrittenMemory, writeVerb, maxDepth);
        }
        catch (InvalidOperationException exception)
        {
            throw Depth("array", writeVerb, resultClause, exception);
        }
    }

    internal static async ValueTask<JsonElement> ArrayAsync(IAsyncEnumerable<object?> elements,
        string? writeVerb, int maxDepth, string resultClause)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = ConstructorWriter(buffer, writeVerb, maxDepth))
            {
                writer.WriteStartArray();
                await foreach (var element in elements.ConfigureAwait(false)) { WriteElement(writer, element, writeVerb); }
                writer.WriteEndArray();
            }
            return ReadConstructor(buffer.WrittenMemory, writeVerb, maxDepth);
        }
        catch (InvalidOperationException exception) { throw Depth("array", writeVerb, resultClause, exception); }
    }

    private static Utf8JsonWriter ConstructorWriter(ArrayBufferWriter<byte> buffer, string? writeVerb, int maxDepth)
        => new(buffer, new JsonWriterOptions { Encoder = DocumentJsonEncoder.Instance, MaxDepth = writeVerb is not null ? 128 : maxDepth });

    private static void WriteElement(Utf8JsonWriter writer, object? element, string? writeVerb)
        => Write(writer, element is DocumentMissing ? null : element, writeVerb);

    private static JsonElement ReadConstructor(ReadOnlyMemory<byte> bytes, string? writeVerb, int maxDepth)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = writeVerb is not null ? 128 : maxDepth });
        return json.RootElement.Clone();
    }

    internal static void ValidateDepth(object? value, int remaining, string? writeVerb = null,
        string resultClause = "SELECT", string constructor = "projection", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is not JsonElement json || json.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) { return; }
        ValidateJsonDepth(json, remaining, writeVerb, resultClause, constructor, cancellationToken);
    }

    private static void ValidateJsonDepth(JsonElement json, int remaining, string? writeVerb,
        string resultClause, string constructor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (json.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) { return; }
        if (remaining == 0) { throw Depth(constructor, writeVerb, resultClause, null); }
        if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.EnumerateArray())
            { ValidateJsonDepth(item, remaining - 1, writeVerb, resultClause, constructor, cancellationToken); }
        }
        else
        {
            foreach (var member in json.EnumerateObject())
            { ValidateJsonDepth(member.Value, remaining - 1, writeVerb, resultClause, constructor, cancellationToken); }
        }
    }

    private static DocumentPlanningException Depth(string constructor, string? writeVerb, string resultClause, Exception? exception)
        => writeVerb is not null
            ? new DocumentPlanningException("CD02008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteJsonInvalid, writeVerb), innerException: exception)
            : new DocumentPlanningException("CD05015", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ResultConstructorDepthExceeded, resultClause, constructor), innerException: exception);

    internal static void Write(Utf8JsonWriter writer, object? value, string? writeVerb = null)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case string text: writer.WriteStringValue(text); break;
            case JsonElement json: json.WriteTo(writer); break;
            default: throw new DocumentPlanningException("CD02008", writeVerb is null ? ErrorMessages.JsonScalarInvalid :
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.WriteValueMissing, writeVerb));
        }
    }
}
