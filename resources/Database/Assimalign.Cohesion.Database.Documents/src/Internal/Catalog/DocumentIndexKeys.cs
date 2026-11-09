using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
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
    // Ordinary maintenance keeps its existing allocation path. Build extraction
    // additionally admits workspace and checks raw lengths before materializing keys.
    internal static HashSet<IndexKey> Read(ReadOnlyMemory<byte> content, DocumentIndexMetadata metadata,
        out bool distinct, System.Threading.CancellationToken cancellationToken = default, Action<long>? reserve = null,
        Action<object>? parsed = null)
        => reserve is null ? ReadStored(content, metadata, out distinct, cancellationToken, parsed: parsed)
            : ReadBuild(content, metadata, out distinct, cancellationToken, reserve, parsed);
    private static HashSet<IndexKey> ReadBuild(ReadOnlyMemory<byte> content, DocumentIndexMetadata metadata,
        out bool distinct, System.Threading.CancellationToken cancellationToken = default, Action<long>? reserve = null,
        Action<object>? parsed = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reserve is not null)
        {
            long pathCharacters = 0;
            foreach (var key in metadata.Keys) { pathCharacters += key.Path.Length + (key.ArraySource?.Length ?? 0); }
            // Count without allocating. DOM metadata has 12-byte token rows and
            // pooled arrays grow by powers of two; include the old growth buffer.
            // Large unindexed strings must not be charged as millions of tokens.
            long tokens = 0;
            int depth = 0;
            var reader = new Utf8JsonReader(content.Span, new JsonReaderOptions { MaxDepth = 128 });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                tokens++;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                { depth = Math.Max(depth, reader.CurrentDepth + 1); }
            }
            long initial = content.Length + 12L;
            if (initial is > 1024 * 1024 and <= 4 * 1024 * 1024) { initial = 1024 * 1024; }
            long metadataBytes = (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(16L, initial));
            long metadataLive = metadataBytes;
            long rows = tokens * 12L;
            while (metadataBytes < rows)
            {
                long grown = metadataBytes * 2;
                // Returned growth buffers can remain resident in ArrayPool while
                // parsing continues. Include those buffers in a cold-pool bound.
                metadataLive += grown;
                metadataBytes = grown;
            }
            if (rows <= metadataBytes / 2)
            {
                long trimmed = (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(16L, rows));
                metadataLive += trimmed;
            }
            // JsonDocument starts with 64 eight-byte stack rows even for a shallow
            // document; include the old buffer while a deeper stack grows.
            long stackBytes = (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(512, depth * 8));
            long stackPeak = stackBytes == 512 ? stackBytes : stackBytes + stackBytes / 2;
            reserve(320L + metadataLive + stackPeak + pathCharacters * 80L);
        }
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 128 });
        parsed?.Invoke(document);
        var result = new HashSet<IndexKey>();
        var paths = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        List<object> Path(string path)
        {
            if (!paths.TryGetValue(path, out var parts))
            { paths.Add(path, parts = path.Length == 0 ? [] : ParsePath(path)); }
            return parts;
        }
        long temporary = 0;
        void ReserveTemporary(int length)
        {
            // Component arrays, the candidate composite, writer scratch and aligned
            // headers coexist before a candidate enters the retained key set.
            long bytes = 512L + 32L * metadata.Keys.Count + 3L * length;
            if (bytes > temporary) { reserve?.Invoke(bytes - temporary); temporary = bytes; }
        }
        ReserveTemporary(0);
        var components = new byte[metadata.Keys.Count][];
        int scalarLength = 0;
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
            components[i] = Component(value, value.ValueKind, metadata, key.Path, ref unknown,
                1024 - scalarLength, ReserveTemporary);
            scalarLength += components[i].Length;
        }
        if (arrayIndex < 0) { Add(default, reserveKey: reserve); }
        else
        {
            var key = metadata.Keys[arrayIndex];
            var source = Navigate(document.RootElement, Path(key.ArraySource!));
            if (source.ValueKind == JsonValueKind.Undefined) { Add(default, reserveKey: reserve); }
            else if (source.ValueKind != JsonValueKind.Array)
            {
                // ARRAY over a present non-array produces NULL, matching #1728.
                Add(default, JsonValueKind.Null, reserve);
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
                    Add(value, reserveKey: reserve);
                }
            }
        }
        distinct = unknown;
        return result;

        void Add(JsonElement element, JsonValueKind? forcedKind = null, Action<long>? reserveKey = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arrayIndex >= 0)
            {
                var kind = forcedKind ?? element.ValueKind;
                if (arrayIndex == 0 && kind == JsonValueKind.Undefined && !metadata.IncludeMissing) { return; }
                components[arrayIndex] = Component(element, kind, metadata, metadata.Keys[arrayIndex].Path, ref unknown, 1024 - scalarLength, ReserveTemporary);
            }
            int length = 0;
            foreach (var component in components)
            {
                if (component.Length > 1024 - length) { throw new DocumentCatalogException("CD02026", ErrorMessages.IndexKeyTooLarge); }
                length += component.Length;
            }
            ReserveTemporary(length);
            var bytes = new byte[length];
            int offset = 0;
            foreach (var component in components) { component.CopyTo(bytes, offset); offset += component.Length; }
            var encoded = new IndexKey(bytes);
            if (reserveKey is null) { result.Add(encoded); return; }
            if (!result.Contains(encoded))
            {
                // Key bytes plus aligned array/header and HashSet growth peaks.
                reserveKey(128L + encoded.Length);
                result.Add(encoded);
            }
        }
    }

    private static byte[] Component(JsonElement value, JsonValueKind kind, DocumentIndexMetadata metadata, string path, ref bool distinct, int remaining, Action<int> reserveTemporary)
    {
        if (remaining < 1) { throw new DocumentCatalogException("CD02026", ErrorMessages.IndexKeyTooLarge); }
        reserveTemporary(1024 - remaining);
        switch (kind)
        {
            case JsonValueKind.Undefined: distinct = true; return [0];
            case JsonValueKind.Null: distinct = true; return [1];
            case JsonValueKind.False: return [2];
            case JsonValueKind.True: return [3];
            case JsonValueKind.Number:
                decimal number = value.GetDecimal();
                int numberLength = NumberLength(number);
                if (numberLength > remaining) { throw new DocumentCatalogException("CD02026", ErrorMessages.IndexKeyTooLarge); }
                reserveTemporary(1024 - remaining + numberLength);
                return Encode(number).Encoded.ToArray();
            case JsonValueKind.String:
                // The raw byte view borrows the DOM. Even all-escaped JSON needs
                // at least one UTF-16 unit per six bytes; reject before decoding.
                var raw = JsonMarshal.GetRawUtf8Value(value);
                if ((raw.Length - 2L) / 6L * 2L + 4L > remaining)
                { throw new DocumentCatalogException("CD02026", ErrorMessages.IndexKeyTooLarge); }
                var reader = new Utf8JsonReader(raw);
                reader.Read();
                Span<char> characters = stackalloc char[raw.Length]; // At most 3,068 units.
                int count = reader.CopyString(characters);
                int length = 4;
                foreach (char character in characters[..count])
                { length += 2 + (character <= 255 ? 1 : 0) + ((character & 255) == 0 ? 1 : 0); }
                if (length > remaining) { throw new DocumentCatalogException("CD02026", ErrorMessages.IndexKeyTooLarge); }
                reserveTemporary(1024 - remaining + length);
                var encoded = new byte[length];
                encoded[0] = 5;
                encoded[1] = (byte)DatabaseType.Binary;
                int at = 2;
                foreach (char character in characters[..count])
                {
                    byte high = (byte)(character >> 8), low = (byte)character;
                    encoded[at++] = high;
                    if (high == 0) { encoded[at++] = 255; }
                    encoded[at++] = low;
                    if (low == 0) { encoded[at++] = 255; }
                }
                // The zero-initialized final pair terminates the binary component.
                return encoded;
            default:
                if (metadata.IsUnique) { throw DocumentConstraintViolationException.NonScalar(metadata.Name, path); }
                return [(byte)(kind == JsonValueKind.Array ? 6 : 7)];
        }
    }

    private static int NumberLength(decimal value)
    {
        if (value == 0) { return 3; }
        Span<char> text = stackalloc char[64];
        value.TryFormat(text, out int count, provider: CultureInfo.InvariantCulture);
        int first = 0, last = count - 1;
        while (!char.IsAsciiDigit(text[first]) || text[first] == '0') { first++; }
        while (!char.IsAsciiDigit(text[last]) || text[last] == '0') { last--; }
        int digits = 0;
        for (int at = first; at <= last; at++) { if (char.IsAsciiDigit(text[at])) { digits++; } }
        return 5 + digits;
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
            else
            {
                if (value.ValueKind != JsonValueKind.Object) { return default; }
                bool found = false;
                JsonElement next = default;
                foreach (var property in value.EnumerateObject())
                {
                    if (!property.NameEquals((string)segment)) { continue; }
                    if (found) { throw new JsonException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DocumentStorageValidateJson, segment)); }
                    found = true;
                    next = property.Value;
                }
                if (!found) { return default; }
                value = next;
            }
        }
        return value;
    }

    private static HashSet<IndexKey> ReadStored(ReadOnlyMemory<byte> content, DocumentIndexMetadata metadata,
        out bool distinct, System.Threading.CancellationToken cancellationToken = default, Action<long>? reserve = null,
        Action<object>? parsed = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reserve is not null)
        {
            long pathCharacters = 0;
            foreach (var key in metadata.Keys) { pathCharacters += key.Path.Length + (key.ArraySource?.Length ?? 0); }
            // Count without allocating. DOM metadata has 12-byte token rows and
            // pooled arrays grow by powers of two; include the old growth buffer.
            // Large unindexed strings must not be charged as millions of tokens.
            long tokens = 0;
            int depth = 0;
            var reader = new Utf8JsonReader(content.Span, new JsonReaderOptions { MaxDepth = 128 });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                tokens++;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                { depth = Math.Max(depth, reader.CurrentDepth + 1); }
            }
            long initial = content.Length + 12L;
            if (initial is > 1024 * 1024 and <= 4 * 1024 * 1024) { initial = 1024 * 1024; }
            long metadataBytes = (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(16L, initial));
            long metadataLive = metadataBytes;
            long rows = tokens * 12L;
            while (metadataBytes < rows)
            {
                long grown = metadataBytes * 2;
                // Returned growth buffers can remain resident in ArrayPool while
                // parsing continues. Include those buffers in a cold-pool bound.
                metadataLive += grown;
                metadataBytes = grown;
            }
            if (rows <= metadataBytes / 2)
            {
                long trimmed = (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(16L, rows));
                metadataLive += trimmed;
            }
            // JsonDocument starts with 64 eight-byte stack rows even for a shallow
            // document; include the old buffer while a deeper stack grows.
            long stackBytes = (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(512, depth * 8));
            long stackPeak = stackBytes == 512 ? stackBytes : stackBytes + stackBytes / 2;
            reserve(320L + metadataLive + stackPeak + pathCharacters * 80L);
        }
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 128 });
        parsed?.Invoke(document);
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
            var value = NavigateStored(document.RootElement, Path(key.Path));
            if (i == 0 && value.ValueKind == JsonValueKind.Undefined && !metadata.IncludeMissing)
            { distinct = false; return result; }
            components[i] = ComponentStored(value, value.ValueKind, metadata, key.Path, ref unknown);
        }
        if (arrayIndex < 0) { Add(default, reserveKey: reserve); }
        else
        {
            var key = metadata.Keys[arrayIndex];
            var source = NavigateStored(document.RootElement, Path(key.ArraySource!));
            if (source.ValueKind == JsonValueKind.Undefined) { Add(default, reserveKey: reserve); }
            else if (source.ValueKind != JsonValueKind.Array)
            {
                // ARRAY over a present non-array produces NULL, matching #1728.
                Add(default, JsonValueKind.Null, reserve);
            }
            else
            {
                foreach (var element in source.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (key.When is not null && Evaluate(key.When, element, Path) is not true) { continue; }
                    var value = NavigateStored(element, Path(key.Path));
                    // Comprehensions omit MISSING projections. This restricted index
                    // stores scalar projections only, never lossy complex element tags.
                    if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Array or JsonValueKind.Object) { continue; }
                    Add(value, reserveKey: reserve);
                }
            }
        }
        distinct = unknown;
        return result;

        void Add(JsonElement element, JsonValueKind? forcedKind = null, Action<long>? reserveKey = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arrayIndex >= 0)
            {
                var kind = forcedKind ?? element.ValueKind;
                if (arrayIndex == 0 && kind == JsonValueKind.Undefined && !metadata.IncludeMissing) { return; }
                components[arrayIndex] = ComponentStored(element, kind, metadata, metadata.Keys[arrayIndex].Path, ref unknown);
            }
            var bytes = new List<byte>();
            foreach (var component in components)
            {
                bytes.AddRange(component);
                if (bytes.Count > 1024) { throw new DocumentCatalogException("CD02026", ErrorMessages.IndexKeyTooLarge); }
            }
            var encoded = new IndexKey(bytes.ToArray());
            if (reserveKey is null) { result.Add(encoded); return; }
            if (!result.Contains(encoded))
            {
                // Key bytes plus aligned array/header and HashSet growth peaks.
                reserveKey(128L + encoded.Length);
                result.Add(encoded);
            }
        }
    }

    private static byte[] ComponentStored(JsonElement value, JsonValueKind kind, DocumentIndexMetadata metadata, string path, ref bool distinct)
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

    private static JsonElement NavigateStored(JsonElement value, List<object> parts)
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

    // Walk only the requested Documents components. Numeric/binary payloads are
    // self-delimiting DatabaseKeyWriter encodings; trailing physical keys do not
    // participate in an ORDER BY tie. No decoded values or key copies are needed.
    internal static ReadOnlyMemory<byte> OrderPrefix(IndexKey key, int components)
    {
        var bytes = key.Encoded.Span;
        int at = 0;
        for (int component = 0; component < components; component++)
        {
            if (at >= bytes.Length) { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
            byte tag = bytes[at++];
            if (tag is 0 or 1 or 2 or 3 or 6 or 7) { continue; }
            if (tag == 4)
            {
                if (at + 1 >= bytes.Length || bytes[at++] != (byte)DatabaseType.Decimal)
                { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                byte sign = bytes[at++];
                if (sign == 1) { continue; }
                if (sign is not (0 or 2) || at >= bytes.Length)
                { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                at++; // exponent
                int digits = 0;
                while (true)
                {
                    if (at >= bytes.Length) { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                    byte digit = sign == 0 ? (byte)~bytes[at++] : bytes[at++];
                    if (digit == 0 && digits > 0) { break; }
                    if (digit is < 1 or > 10) { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                    digits++;
                }
            }
            else if (tag == 5)
            {
                if (at >= bytes.Length || bytes[at++] != (byte)DatabaseType.Binary)
                { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                while (true)
                {
                    if (at >= bytes.Length) { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                    if (bytes[at++] != 0) { continue; }
                    if (at >= bytes.Length) { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                    byte marker = bytes[at++];
                    if (marker == 0) { break; }
                    if (marker != 255) { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
                }
            }
            else { throw new DatabaseTypeException(ErrorMessages.OrderedIndexKeyMalformed); }
        }
        return key.Encoded[..at];
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
