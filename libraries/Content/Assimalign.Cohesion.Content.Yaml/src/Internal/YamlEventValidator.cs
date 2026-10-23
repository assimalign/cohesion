using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assimalign.Cohesion.Content.Yaml.Internal;

/// <summary>Checks final event nesting and alias expansion without visiting an expanded graph.</summary>
internal static class YamlEventValidator
{
    internal static void Validate(IReadOnlyList<YamlEvent> events, YamlParserOptions options, string source)
    {
        var anchors = new Dictionary<string, Expansion>(StringComparer.Ordinal);
        var open = new Stack<Expansion>();
        var remaining = options.MaxAliasExpansion;

        foreach (var current in events)
        {
            if (current.Kind == YamlEventKind.DocumentStart)
            {
                anchors.Clear();
                remaining = options.MaxAliasExpansion;
                continue;
            }

            if (current.Kind is YamlEventKind.SequenceEnd or YamlEventKind.MappingEnd)
            {
                var completed = open.Pop();
                completed.IsComplete = true;
                if (open.TryPeek(out var parent)) { parent.Add(completed, options.MaxAliasExpansion); }
                continue;
            }

            if (current.Kind is not (YamlEventKind.SequenceStart or YamlEventKind.MappingStart or YamlEventKind.Scalar or YamlEventKind.Alias))
            {
                continue;
            }

            Expansion size;
            if (current.Kind == YamlEventKind.Alias)
            {
                if (current.Value is null || !anchors.TryGetValue(current.Value, out var target))
                {
                    throw YamlException.At(string.Format(CultureInfo.CurrentCulture, ErrorMessages.AliasAnchorMissing, current.Value),
                        ContentFormatErrorCode.InvalidStructure, current.Line, current.Column, source);
                }

                // Cycles are retained as references. Count a back-edge once; never walk it.
                size = target.IsComplete ? target : new Expansion { IsComplete = true };
                if (size.ExceedsBudget || size.Count > remaining)
                {
                    throw YamlException.At(ErrorMessages.AliasExpansionLimitExceeded, ContentFormatErrorCode.LimitExceeded,
                        current.Line, current.Column, source);
                }
                remaining -= size.Count;
            }
            else
            {
                size = new Expansion { IsComplete = current.Kind == YamlEventKind.Scalar };
                if (current.Anchor is { } anchor) { anchors[anchor.Value] = size; }
            }

            if (current.Kind is YamlEventKind.SequenceStart or YamlEventKind.MappingStart)
            {
                // The parser inserts some mapping starts retroactively, around a completed key.
                // Validate that key's final depth here as well as checking before descent.
                if (open.Count >= options.MaxDepth)
                {
                    throw YamlException.At(ErrorMessages.NestingLimitExceeded, ContentFormatErrorCode.LimitExceeded,
                        current.Line, current.Column, source);
                }
                open.Push(size);
            }
            else if (open.TryPeek(out var parent))
            {
                parent.Add(size, options.MaxAliasExpansion);
            }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("Expansion")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class Expansion
    {
        internal long Count { get; private set; } = 1;
        internal bool IsComplete { get; set; }
        internal bool ExceedsBudget { get; private set; }

        internal void Add(Expansion child, long limit)
        {
            // Saturate without overflow, including a caller-supplied long.MaxValue budget.
            if (ExceedsBudget || child.ExceedsBudget || Count > limit || child.Count > limit - Count)
            {
                ExceedsBudget = true;
                return;
            }
            Count += child.Count;
        }

        private sealed class DebugView
        {
            public DebugView(Expansion value)
            {
            }
        }
    }
}
