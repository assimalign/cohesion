using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Language;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentJsonPatcher
{
    internal static byte[] Patch(JsonElement document, IReadOnlyList<DocumentUpdateTarget> targets,
        object?[] values, Location? location, CancellationToken cancellationToken)
    {
        var root = new PatchNode(default, targets[0]);
        for (int i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            bool writes = values[i] is not DocumentMissing || target.Segments[^1].Index is not null;
            var node = root;
            node.HasWrites |= writes;
            foreach (var segment in target.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new PatchNode(segment, target);
                    node.Children.Add(segment, child);
                    node.Ordered.Add(child);
                }
                node = child;
                node.HasWrites |= writes;
            }
            node.Leaf = i;
        }
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            Rewrite(buffer, document, root, values, 0, cancellationToken);
            byte[] content = buffer.WrittenSpan.ToArray();
            DocumentStorage.ValidateContent(content);
            return content;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new DocumentPlanningException("CD02018", ErrorMessages.UpdateJsonInvalid, location, exception);
        }
    }

    private static void Rewrite(IBufferWriter<byte> buffer, JsonElement current, PatchNode node,
        object?[] values, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.Leaf is int leaf)
        {
            try
            {
                // Count the enclosing containers even though this writer serializes only the target.
                if (depth >= 128 && values[leaf] is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
                {
                    throw new DocumentPlanningException("CD02018", ErrorMessages.UpdateJsonInvalid, node.Target.Path.Location);
                }
                using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
                {
                    MaxDepth = Math.Max(1, 128 - depth),
                    Encoder = DocumentJsonEncoder.Instance,
                });
                // A MISSING array element is JSON null; missing object members are omitted by the caller.
                DocumentJsonValue.Write(writer, values[leaf] is DocumentMissing ? null : values[leaf]);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new DocumentPlanningException("CD02018", ErrorMessages.UpdateJsonInvalid, node.Target.Path.Location, exception);
            }
            return;
        }
        if (depth >= 128)
        {
            throw new DocumentPlanningException("CD02018", ErrorMessages.UpdateJsonInvalid, node.Target.Path.Location);
        }
        if (current.ValueKind == JsonValueKind.Array)
        {
            int length = current.GetArrayLength();
            foreach (var child in node.Ordered)
            {
                if (child.Segment.Index is not int index || index >= length) { throw PathFailure(child); }
            }
            buffer.Write("["u8);
            int ordinal = 0;
            foreach (var element in current.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ordinal > 0) { buffer.Write(","u8); }
                if (node.Children.TryGetValue(new OqlPathSegment(null, ordinal++), out var child))
                { Rewrite(buffer, element, child, values, depth + 1, cancellationToken); }
                else { Raw(buffer, element.GetRawText()); }
            }
            buffer.Write("]"u8);
            return;
        }
        if (current.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) { throw PathFailure(node); }
        foreach (var child in node.Ordered)
        {
            if (child.Segment.Name is null) { throw PathFailure(child); }
        }
        buffer.Write("{"u8);
        bool hasMember = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (current.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in current.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Children.TryGetValue(new OqlPathSegment(property.Name, null), out var child))
                {
                    seen.Add(property.Name);
                    Member(property.Name, property.Value, child, property);
                }
                else
                {
                    Separator();
                    // JsonProperty.ToString retains both the encoded name and the complete raw value.
                    Raw(buffer, property.ToString());
                }
            }
        }
        // New members follow the statement's target order. Existing members keep their positions.
        foreach (var child in node.Ordered)
        {
            string name = child.Segment.Name!;
            if (seen.Contains(name)) { continue; }
            if (child.HasWrites) { Member(name, default, child); }
            else { ValidateAbsent(child, cancellationToken); }
        }
        buffer.Write("}"u8);

        void Separator()
        {
            if (hasMember) { buffer.Write(","u8); }
            hasMember = true;
        }

        void Member(string name, JsonElement value, PatchNode child, JsonProperty? property = null)
        {
            if (child.Leaf is int index && values[index] is DocumentMissing) { return; }
            Separator();
            if (property is { } existing)
            {
                string raw = existing.ToString();
                int end = 1;
                for (; end < raw.Length; end++)
                {
                    if (raw[end] == '\\') { end++; }
                    else if (raw[end] == '"') { break; }
                }
                Raw(buffer, raw.AsSpan(0, end + 1));
            }
            else
            {
                buffer.Write("\""u8);
                buffer.Write(JsonEncodedText.Encode(name, DocumentJsonEncoder.Instance).EncodedUtf8Bytes);
                buffer.Write("\""u8);
            }
            buffer.Write(":"u8);
            Rewrite(buffer, value, child, values, depth + 1, cancellationToken);
        }
    }

    private static void Raw(IBufferWriter<byte> buffer, ReadOnlySpan<char> text)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        Encoding.UTF8.GetBytes(text, buffer.GetSpan(length));
        buffer.Advance(length);
    }

    private static void ValidateAbsent(PatchNode node, CancellationToken cancellationToken)
    {
        // Missing object members are harmless for removal, but an index always needs an array.
        // Walk iteratively: a removal-only path need not fit the storage depth limit.
        var pending = new Stack<PatchNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Segment.Index is not null) { throw PathFailure(current); }
            foreach (var child in current.Ordered) { pending.Push(child); }
        }
    }

    private static DocumentPlanningException PathFailure(PatchNode node)
    {
        string path = string.Join("", node.Target.Path.Segments.Select((segment, index) => segment.Index is int ordinal
            ? $"[{ordinal}]" : segment.Name is { Length: > 0 } name && (char.IsLetter(name[0]) || name[0] == '_') &&
                name.All(character => char.IsLetterOrDigit(character) || character == '_')
                ? (index == 0 ? name : "." + name) : "['" + segment.Name!.Replace("'", "''", StringComparison.Ordinal) + "']"));
        return new DocumentPlanningException("CD02016", string.Format(CultureInfo.CurrentCulture,
            ErrorMessages.UpdatePathShapeInvalid, path), node.Target.Path.Location);
    }

    private sealed class PatchNode
    {
        internal PatchNode(OqlPathSegment segment, DocumentUpdateTarget target)
        {
            Segment = segment;
            Target = target;
        }

        internal OqlPathSegment Segment { get; }
        internal DocumentUpdateTarget Target { get; }
        internal Dictionary<OqlPathSegment, PatchNode> Children { get; } = new();
        internal List<PatchNode> Ordered { get; } = new();
        internal int? Leaf { get; set; }
        internal bool HasWrites { get; set; }
    }
}
