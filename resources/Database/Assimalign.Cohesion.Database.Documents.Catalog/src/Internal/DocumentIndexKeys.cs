using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

internal static class DocumentIndexKeys
{
    internal static void ValidatePath(string path) => ParsePath(path);

    internal static IndexKey? Read(ReadOnlyMemory<byte> content, string path)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 128 });
        var value = document.RootElement;
        foreach (var segment in ParsePath(path))
        {
            if (segment is int index)
            {
                if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength())
                {
                    return null;
                }
                value = value[index];
            }
            else if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty((string)segment, out value))
            {
                return null;
            }
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => Encode(true),
            JsonValueKind.False => Encode(false),
            JsonValueKind.Number => Encode(value.GetDecimal()),
            JsonValueKind.String => Encode(value.GetString()!),
            _ => null
        };
    }

    internal static IndexKey Encode(object value)
    {
        var writer = new DatabaseKeyWriter();
        switch (value)
        {
            case bool boolean:
                return IndexKey.From(writer.AppendBoolean(boolean));
            case string text:
                // UTF-16 big-endian code units match StringComparer.Ordinal exactly,
                // including ordering supplementary characters against BMP text.
                var bytes = new byte[checked(text.Length * 2)];
                for (int i = 0; i < text.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), text[i]);
                }
                return IndexKey.From(writer.AppendBinary(bytes));
            case decimal number:
                return IndexKey.From(writer.AppendDecimal(number));
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return IndexKey.From(writer.AppendDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture)));
            default:
                throw new ArgumentException("Index bounds require a boolean, decimal, integer, or string.", nameof(value));
        }
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
