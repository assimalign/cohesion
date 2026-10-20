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
    internal static IndexKey? Read(ReadOnlyMemory<byte> content, DocumentIndexMetadata metadata, out bool distinct)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 128 });
        var bytes = new List<byte>();
        distinct = false;
        for (int i = 0; i < metadata.Paths.Count; i++)
        {
            var value = document.RootElement;
            foreach (var segment in ParsePath(metadata.Paths[i]))
            {
                if (segment is int index)
                {
                    if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength())
                    { value = default; break; }
                    value = value[index];
                }
                else if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty((string)segment, out value))
                { value = default; break; }
            }
            if (i == 0 && value.ValueKind == JsonValueKind.Undefined) { return null; }
            switch (value.ValueKind)
            {
                case JsonValueKind.Undefined: bytes.Add(0); distinct = true; break;
                case JsonValueKind.Null: bytes.Add(1); distinct = true; break;
                case JsonValueKind.False: bytes.Add(2); break;
                case JsonValueKind.True: bytes.Add(3); break;
                case JsonValueKind.Number: bytes.AddRange(Encode(value.GetDecimal()).Encoded.ToArray()); break;
                case JsonValueKind.String: bytes.AddRange(Encode(value.GetString()!).Encoded.ToArray()); break;
                default:
                    if (metadata.IsUnique)
                    {
                        throw DocumentConstraintViolationException.NonScalar(metadata.Name, metadata.Paths[i]);
                    }
                    bytes.Add((byte)(value.ValueKind == JsonValueKind.Array ? 6 : 7));
                    break;
            }
        }
        return new IndexKey(bytes.ToArray());
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
        else { throw new ArgumentException("Index bounds require a boolean, decimal, integer, or string.", nameof(value)); }
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

    private static List<object> ParsePath(string path)
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
                throw new ArgumentException("Malformed index path separator.", nameof(path));
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
                throw new ArgumentException("An index path requires nonempty field names.", nameof(path));
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
                throw new ArgumentException("Quoted index path properties require doubled apostrophes and a closing bracket.", nameof(path));
            }
            throw new ArgumentException("Quoted index path properties require a closing apostrophe and bracket.", nameof(path));
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
                throw new ArgumentException("Index array subscripts require a nonnegative Int32.", nameof(path));
            }
            parts.Add(index);
            cursor++;
        }
    }
}
