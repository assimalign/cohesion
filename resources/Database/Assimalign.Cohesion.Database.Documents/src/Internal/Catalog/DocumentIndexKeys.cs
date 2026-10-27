using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Catalog;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentIndexKeys
{
    internal static void ValidatePath(string path) => ParsePath(path);

    // Each component begins with a Documents-owned collation byte. Scalar payloads
    // retain DatabaseKeyWriter's self-delimiting representation; no tag is 0xFF.
    internal static HashSet<IndexKey> Read(ReadOnlyMemory<byte> content, DocumentIndexMetadata metadata,
        out bool distinct, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 128 });
        var result = new HashSet<IndexKey>();
        var paths = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        List<object> Path(string path)
        {
            if (!paths.TryGetValue(path, out var parts))
            { paths.Add(path, parts = path.Length == 0 ? [] : ParsePath(path)); }
            return parts;
        }
        var components = new byte[metadata.Keys.Count][];
        int arrayIndex = -1;
        bool unknown = false;
        for (int i = 0; i < metadata.Keys.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = metadata.Keys[i];
            if (key.ArraySource is not null) { arrayIndex = i; continue; }
            var value = Navigate(document.RootElement, Path(key.Path));
            if (i == 0 && value.ValueKind == JsonValueKind.Undefined && !metadata.IncludeMissing)
            { distinct = false; return result; }
            components[i] = Component(value, value.ValueKind, metadata, key.Path, ref unknown);
        }
        if (arrayIndex < 0) { Add(default); }
        else
        {
            var key = metadata.Keys[arrayIndex];
            var source = Navigate(document.RootElement, Path(key.ArraySource!));
            if (source.ValueKind == JsonValueKind.Undefined) { Add(default); }
            else if (source.ValueKind != JsonValueKind.Array)
            {
                // ARRAY over a present non-array produces NULL, matching #1728.
                Add(default, JsonValueKind.Null);
            }
            else
            {
                foreach (var element in source.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (key.When is not null && Evaluate(key.When, element, Path) is not true) { continue; }
                    var value = Navigate(element, Path(key.Path));
                    // Comprehensions omit MISSING projections. This restricted index
                    // stores scalar projections only, never lossy complex element tags.
                    if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Array or JsonValueKind.Object) { continue; }
                    Add(value);
                }
            }
        }
        distinct = unknown;
        return result;

        void Add(JsonElement element, JsonValueKind? forcedKind = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arrayIndex >= 0)
            {
                var kind = forcedKind ?? element.ValueKind;
                if (arrayIndex == 0 && kind == JsonValueKind.Undefined && !metadata.IncludeMissing) { return; }
                components[arrayIndex] = Component(element, kind, metadata, metadata.Keys[arrayIndex].Path, ref unknown);
            }
            var bytes = new List<byte>();
            foreach (var component in components)
            {
                bytes.AddRange(component);
                if (bytes.Count > 1024) { throw new DocumentCatalogException("CD02026", ErrorMessages.IndexKeyTooLarge); }
            }
            result.Add(new IndexKey(bytes.ToArray()));
        }
    }

    private static byte[] Component(JsonElement value, JsonValueKind kind, DocumentIndexMetadata metadata, string path, ref bool distinct)
    {
        switch (kind)
        {
            case JsonValueKind.Undefined: distinct = true; return [0];
            case JsonValueKind.Null: distinct = true; return [1];
            case JsonValueKind.False: return [2];
            case JsonValueKind.True: return [3];
            case JsonValueKind.Number: return Encode(value.GetDecimal()).Encoded.ToArray();
            case JsonValueKind.String: return Encode(value.GetString()!).Encoded.ToArray();
            default:
                if (metadata.IsUnique) { throw DocumentConstraintViolationException.NonScalar(metadata.Name, path); }
                return [(byte)(kind == JsonValueKind.Array ? 6 : 7)];
        }
    }

    private static JsonElement Navigate(JsonElement value, List<object> parts)
    {
        foreach (var segment in parts)
        {
            if (segment is int index)
            {
                if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength()) { return default; }
                value = value[index];
            }
            else if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty((string)segment, out value)) { return default; }
        }
        return value;
    }

    // Only TRUE is observable by WHEN. Collapsing NULL and MISSING to unknown here
    // preserves that result for this closed AND/OR/NOT/comparison grammar.
    private static bool? Evaluate(DocumentIndexPredicate predicate, JsonElement element, Func<string, List<object>> path)
    {
        if (predicate.Kind == 6) { return !Evaluate(predicate.Left!, element, path); }
        if (predicate.Kind is 4 or 5)
        {
            var left = Evaluate(predicate.Left!, element, path);
            if (predicate.Kind == 4 && left is false || predicate.Kind == 5 && left is true) { return left; }
            var right = Evaluate(predicate.Right!, element, path);
            return predicate.Kind == 4 ? left & right : left | right;
        }
        var value = Navigate(element, path(predicate.Path!));
        if (predicate.Kind == 3) { return value.ValueKind == JsonValueKind.Undefined; }
        if (value.ValueKind == JsonValueKind.Undefined) { return null; }
        if (predicate.Kind == 2) { return value.ValueKind == JsonValueKind.Null; }
        if (value.ValueKind == JsonValueKind.Null || predicate.Value is null) { return null; }
        int? comparison = (value.ValueKind, predicate.Value) switch
        {
            (JsonValueKind.Number, decimal number) => value.GetDecimal().CompareTo(number),
            (JsonValueKind.String, string text) => string.CompareOrdinal(value.GetString(), text),
            (JsonValueKind.True or JsonValueKind.False, bool boolean) => value.GetBoolean().CompareTo(boolean),
            _ => null
        };
        return predicate.Comparison switch
        {
            DocumentIndexComparison.Equal => comparison == 0,
            DocumentIndexComparison.NotEqual => comparison != 0,
            DocumentIndexComparison.LessThan => comparison < 0,
            DocumentIndexComparison.LessThanOrEqual => comparison <= 0,
            DocumentIndexComparison.GreaterThan => comparison > 0,
            DocumentIndexComparison.GreaterThanOrEqual => comparison >= 0,
            _ => false
        };
    }

    internal static IndexKey Encode(object value)
    {
        if (value is bool boolean) { return new IndexKey(new byte[] { boolean ? (byte)3 : (byte)2 }); }
        var writer = new DatabaseKeyWriter();
        byte tag;
        if (value is string text)
        {
            tag = 5;
            // UTF-16BE, including surrogate code units, matches StringComparer.Ordinal.
            var bytes = new byte[checked(text.Length * 2)];
            for (int i = 0; i < text.Length; i++)
            { BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), text[i]); }
            writer.AppendBinary(bytes);
        }
        else if (value is decimal or byte or sbyte or short or ushort or int or uint or long or ulong)
        {
            tag = 4;
            writer.AppendDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }
        else { throw new ArgumentException(ErrorMessages.DocumentIndexKeysEncodeArgument, nameof(value)); }
        var payload = IndexKey.From(writer).Encoded;
        var encoded = new byte[payload.Length + 1];
        encoded[0] = tag;
        payload.Span.CopyTo(encoded.AsSpan(1));
        return new IndexKey(encoded);
    }

    internal static IndexKey EncodePrefix(IReadOnlyList<object> values, object? last = null)
    {
        var bytes = new List<byte>();
        foreach (var value in values) { bytes.AddRange(Encode(value).Encoded.ToArray()); }
        if (last is not null) { bytes.AddRange(Encode(last).Encoded.ToArray()); }
        return new IndexKey(bytes.ToArray());
    }

    // SQL's prefix-successor rule, SqlPlanExecutor.BuildIndexRange: a prefix is
    // [P, successor(P)), including all following components of the composite.
    internal static IndexKey? Successor(IndexKey key)
    {
        var bytes = key.Encoded.ToArray();
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            if (bytes[i] == byte.MaxValue) { continue; }
            bytes[i]++;
            return new IndexKey(bytes.AsMemory(0, i + 1));
        }
        return null;
    }

    internal static List<object> ParsePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var parts = new List<object>();
        int cursor = 0;
        if (IsQuotedProperty()) { ReadQuotedProperty(); }
        else { ReadBareProperty(); }

        while (cursor < path.Length)
        {
            if (path[cursor] == '.')
            {
                cursor++;
                ReadBareProperty();
            }
            else if (path[cursor] == '[')
            {
                if (IsQuotedProperty()) { ReadQuotedProperty(); }
                else { ReadArrayIndex(); }
            }
            else
            {
                throw new ArgumentException(ErrorMessages.DocumentIndexKeysParsePathArgument, nameof(path));
            }
        }
        return parts;

        bool IsQuotedProperty() => cursor + 1 < path.Length && path[cursor] == '[' && path[cursor + 1] == '\'';

        void ReadBareProperty()
        {
            int start = cursor;
            while (cursor < path.Length && path[cursor] is not ('.' or '[' or ']'))
            {
                cursor++;
            }
            if (cursor == start)
            {
                throw new ArgumentException(ErrorMessages.DocumentIndexKeysParsePathArgument2, nameof(path));
            }
            parts.Add(path[start..cursor]);
        }

        void ReadQuotedProperty()
        {
            cursor += 2; // ['
            var name = new StringBuilder();
            while (cursor < path.Length)
            {
                char character = path[cursor++];
                if (character != '\'')
                {
                    name.Append(character);
                    continue;
                }
                if (cursor < path.Length && path[cursor] == '\'')
                {
                    name.Append('\'');
                    cursor++;
                    continue;
                }
                if (cursor < path.Length && path[cursor] == ']')
                {
                    cursor++;
                    parts.Add(name.ToString());
                    return;
                }
                throw new ArgumentException(ErrorMessages.DocumentIndexKeysParsePathArgument3, nameof(path));
            }
            throw new ArgumentException(ErrorMessages.DocumentIndexKeysParsePathArgument4, nameof(path));
        }

        void ReadArrayIndex()
        {
            int start = ++cursor;
            while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
            {
                cursor++;
            }
            if (cursor == start || cursor == path.Length || path[cursor] != ']'
                || !int.TryParse(path.AsSpan(start, cursor - start), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                throw new ArgumentException(ErrorMessages.DocumentIndexKeysParsePathArgument5, nameof(path));
            }
            parts.Add(index);
            cursor++;
        }
    }
}
