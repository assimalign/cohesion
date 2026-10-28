using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    private static long GroupValueBytes(object? value, HashSet<object>? retainedValues, DocumentAggregateMemory memory)
    {
        // Every key/alias slot retains its overhead. A new representation also owns
        // its payload and a tracking entry; sharing never follows structural equality.
        if (retainedValues is null) { return 64L + memory.ValueBytes(value); }
        if (value is null || !retainedValues.Add(value)) { return 64L; }
        return 128L + memory.ValueBytes(value);
    }

    private static (JsonElement Value, long RetainedBytes) GroupArray(OqlSelectExpression query, IReadOnlyList<DocumentEvaluationContext> group,
        DocumentAggregateMemory memory, CancellationToken cancellationToken)
    {
        using var scratch = memory.Reserve();
        using var retained = memory.Reserve();
        scratch.Charge(128);
        var wrappers = new List<object?>();
        // Final JSON clone: ownership, array delimiters/tokens and commas. Wrapper
        // payloads/tokens are measured once, independently of their temporary owners.
        long arrayBytes = 128L + 2L + 24L + Math.Max(0, group.Count - 1);
        foreach (var member in group)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wrapper = DocumentJsonValue.Object(Members(member));
            scratch.Retain(wrapper);
            arrayBytes += memory.ValueBytes(wrapper) - 128L;
            wrappers.Add(wrapper);
        }
        // The array coexists with scratch until serialization completes, then remains
        // live through HAVING, projection and ORDER BY. Reserve before materializing it.
        retained.Charge(64L + arrayBytes + 64L);
        var array = DocumentJsonValue.Array(wrappers);
        return (array, retained.Detach());

        IEnumerable<KeyValuePair<string, object?>> Members(DocumentEvaluationContext member)
        {
            yield return new(query.Alias ?? query.Collection, member.Content);
            // Bindings are distinct by planning, so their reverse linked-list order
            // affects only JSON member order, which is not part of object equality.
            for (var binding = member.Bindings; binding is not null; binding = binding.Parent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(binding.Name, binding.Value);
            }
        }
    }

    private sealed class GroupValueComparer : IEqualityComparer<object>
    {
        internal static GroupValueComparer Instance { get; } = new();

        public new bool Equals(object? left, object? right)
            => ReferenceEquals(left, right) || left is JsonElement leftJson && right is JsonElement rightJson && leftJson.Equals(rightJson);

        // JsonElement equality identifies the same owner/element, not equal JSON
        // content. Independently serialized wrappers and arrays remain fully charged.
        public int GetHashCode(object value)
            => value is JsonElement json ? json.GetHashCode() : RuntimeHelpers.GetHashCode(value);
    }
}
