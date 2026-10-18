using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Documents.Storage;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentJsonPatcher
{
    internal static byte[] Patch(JsonElement document, IReadOnlyList<DocumentUpdateTarget> targets,
        object?[] values, CancellationToken cancellationToken)
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
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 128 }))
            {
                Rewrite(writer, document, root, values, cancellationToken);
            }
            byte[] content = buffer.WrittenSpan.ToArray();
            DocumentStorage.ValidateContent(content);
            return content;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new DocumentPlanningException("COHDOC018", ErrorMessages.UpdateJsonInvalid, targets[0].Path.Location, exception);
        }
    }

    private static void Rewrite(Utf8JsonWriter writer, JsonElement current, PatchNode node,
        object?[] values, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.Leaf is int leaf)
        {
            // A MISSING array element is JSON null; missing object members are omitted by the caller.
            DocumentJsonValue.Write(writer, values[leaf] is DocumentMissing ? null : values[leaf]);
            return;
        }
        if (writer.CurrentDepth >= 128)
        {
            throw new DocumentPlanningException("COHDOC018", ErrorMessages.UpdateJsonInvalid, node.Target.Path.Location);
        }
        if (current.ValueKind == JsonValueKind.Array)
        {
            int length = current.GetArrayLength();
            foreach (var child in node.Ordered)
            {
                if (child.Segment.Index is not int index || index >= length) { throw PathFailure(child); }
            }
            writer.WriteStartArray();
            int ordinal = 0;
            foreach (var element in current.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Children.TryGetValue(new OqlPathSegment(null, ordinal++), out var child))
                { Rewrite(writer, element, child, values, cancellationToken); }
                else { element.WriteTo(writer); }
            }
            writer.WriteEndArray();
            return;
        }
        if (current.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) { throw PathFailure(node); }
        foreach (var child in node.Ordered)
        {
            if (child.Segment.Name is null) { throw PathFailure(child); }
        }
        writer.WriteStartObject();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (current.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in current.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Children.TryGetValue(new OqlPathSegment(property.Name, null), out var child))
                {
                    seen.Add(property.Name);
                    Member(property.Name, property.Value, child);
                }
                else { property.WriteTo(writer); }
            }
        }
        // New members follow the statement's target order. Existing members keep their positions.
        foreach (var child in node.Ordered)
        {
            string name = child.Segment.Name!;
            if (!seen.Contains(name) && child.HasWrites) { Member(name, default, child); }
        }
        writer.WriteEndObject();

        void Member(string name, JsonElement value, PatchNode child)
        {
            if (child.Leaf is int index && values[index] is DocumentMissing) { return; }
            writer.WritePropertyName(name);
            Rewrite(writer, value, child, values, cancellationToken);
        }
    }

    private static DocumentPlanningException PathFailure(PatchNode node)
    {
        string path = string.Join("", node.Target.Path.Segments.Select((segment, index) => segment.Index is int ordinal
            ? $"[{ordinal}]" : segment.Name is { Length: > 0 } name && (char.IsLetter(name[0]) || name[0] == '_') &&
                name.All(character => char.IsLetterOrDigit(character) || character == '_')
                ? (index == 0 ? name : "." + name) : "['" + segment.Name!.Replace("'", "''", StringComparison.Ordinal) + "']"));
        return new DocumentPlanningException("COHDOC016", string.Format(CultureInfo.CurrentCulture,
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
