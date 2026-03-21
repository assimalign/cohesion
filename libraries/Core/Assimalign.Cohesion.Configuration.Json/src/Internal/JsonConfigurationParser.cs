using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Json;

using Assimalign.Cohesion.Configuration;

internal static class JsonConfigurationParser
{
    public static async Task ParseAsync(
        Stream stream,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            options,
            cancellationToken).ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException(
                $"The JSON configuration root must be an object, but '{document.RootElement.ValueKind}' was provided.");
        }

        var pathSegments = new List<Key>();

        VisitObject(document.RootElement, pathSegments, entries, cancellationToken);
    }

    private static void VisitObject(
        JsonElement element,
        List<Key> pathSegments,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken)
    {
        bool isEmpty = true;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();

            isEmpty = false;
            pathSegments.Add(new Key(property.Name));

            VisitValue(property.Value, pathSegments, entries, cancellationToken);

            pathSegments.RemoveAt(pathSegments.Count - 1);
        }

        if (isEmpty && pathSegments.Count > 0)
        {
            AddEntry(entries, pathSegments, null);
        }
    }

    private static void VisitArray(
        JsonElement element,
        List<Key> pathSegments,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken)
    {
        int index = 0;
        bool isEmpty = true;

        foreach (JsonElement item in element.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            isEmpty = false;
            pathSegments.Add(new Key(index.ToString(CultureInfo.InvariantCulture)));

            VisitValue(item, pathSegments, entries, cancellationToken);

            pathSegments.RemoveAt(pathSegments.Count - 1);
            index++;
        }

        if (isEmpty && pathSegments.Count > 0)
        {
            AddEntry(entries, pathSegments, null);
        }
    }

    private static void VisitValue(
        JsonElement value,
        List<Key> pathSegments,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                VisitObject(value, pathSegments, entries, cancellationToken);
                break;

            case JsonValueKind.Array:
                VisitArray(value, pathSegments, entries, cancellationToken);
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                AddEntry(entries, pathSegments, value.ToString());
                break;

            case JsonValueKind.Null:
                AddEntry(entries, pathSegments, null);
                break;

            default:
                throw new FormatException(
                    $"The JSON token '{value.ValueKind}' is not supported by the configuration parser.");
        }
    }

    private static void AddEntry(IDictionary<Path, string?> entries, List<Key> pathSegments, string? value)
    {
        if (pathSegments.Count == 0)
        {
            throw new FormatException("The JSON configuration parser cannot assign a value to an empty path.");
        }

        Path path = CreatePath(pathSegments);

        if (entries.ContainsKey(path))
        {
            throw new FormatException($"A duplicate configuration key '{path}' was found while reading JSON.");
        }

        entries.Add(path, value);
    }

    private static Path CreatePath(List<Key> pathSegments)
    {
        var keys = new Key[pathSegments.Count];
        pathSegments.CopyTo(keys, 0);

        return new Path(keys);
    }
}
