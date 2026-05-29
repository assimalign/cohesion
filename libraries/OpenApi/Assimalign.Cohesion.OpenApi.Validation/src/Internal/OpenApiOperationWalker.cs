using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// A single operation discovered while walking a document, paired with its JSON Pointer and the path
/// template it belongs to (empty for webhook operations).
/// </summary>
internal readonly struct OpenApiOperationEntry
{
    public OpenApiOperationEntry(string pointer, string pathKey, bool isPath, OpenApiOperation operation)
    {
        Pointer = pointer;
        PathKey = pathKey;
        IsPath = isPath;
        Operation = operation;
    }

    public string Pointer { get; }

    public string PathKey { get; }

    public bool IsPath { get; }

    public OpenApiOperation Operation { get; }
}

/// <summary>
/// Enumerates every operation in a document (under <c>paths</c> and <c>webhooks</c>, including
/// non-standard additional operations) together with the JSON Pointer that locates it.
/// </summary>
internal static class OpenApiOperationWalker
{
    internal static IEnumerable<OpenApiOperationEntry> Enumerate(OpenApiDocument document)
    {
        if (document.Paths is not null)
        {
            foreach (var entry in EnumeratePathMap(document.Paths.Items, "paths", isPath: true))
            {
                yield return entry;
            }
        }

        foreach (var entry in EnumeratePathMap(document.Webhooks, "webhooks", isPath: false))
        {
            yield return entry;
        }
    }

    private static IEnumerable<OpenApiOperationEntry> EnumeratePathMap(IDictionary<string, OpenApiPathItem> items, string root, bool isPath)
    {
        foreach (var pair in items)
        {
            var item = pair.Value;

            foreach (var operation in item.Operations)
            {
                var pointer = JsonPointer.Of(root, pair.Key, OperationTypeString(operation.Key));
                yield return new OpenApiOperationEntry(pointer, isPath ? pair.Key : string.Empty, isPath, operation.Value);
            }

            foreach (var operation in item.AdditionalOperations)
            {
                var pointer = JsonPointer.Of(root, pair.Key, "additionalOperations", operation.Key);
                yield return new OpenApiOperationEntry(pointer, isPath ? pair.Key : string.Empty, isPath, operation.Value);
            }
        }
    }

    internal static string OperationTypeString(OperationType type) => type switch
    {
        OperationType.Get => "get",
        OperationType.Put => "put",
        OperationType.Post => "post",
        OperationType.Delete => "delete",
        OperationType.Options => "options",
        OperationType.Head => "head",
        OperationType.Patch => "patch",
        OperationType.Trace => "trace",
        _ => type.ToString().ToLowerInvariant()
    };
}
