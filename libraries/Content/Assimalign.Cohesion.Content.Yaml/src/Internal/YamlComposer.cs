using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Content.Yaml.Internal;

/// <summary>
/// Composes the parse-event stream into the document model, resolving aliases to shared node
/// instances. Anchors are registered before children are composed so self-referential structures
/// compose without special cases.
/// </summary>
internal static class YamlComposer
{
    internal static YamlStream Compose(IReadOnlyList<YamlEvent> events, string source)
    {
        var stream = new YamlStream();
        var index = 0;

        Expect(events, ref index, source, YamlEventKind.StreamStart);

        while (events[index].Kind == YamlEventKind.DocumentStart)
        {
            var start = events[index++];
            var document = new YamlDocument { IsExplicit = start.IsExplicit };
            var anchors = new Dictionary<string, YamlNode>();

            if (events[index].Kind != YamlEventKind.DocumentEnd)
            {
                document.Root = ComposeNode(events, ref index, anchors, source);
            }

            Expect(events, ref index, source, YamlEventKind.DocumentEnd);
            stream.Documents.Add(document);
        }

        Expect(events, ref index, source, YamlEventKind.StreamEnd);
        return stream;
    }

    private static YamlNode ComposeNode(IReadOnlyList<YamlEvent> events, ref int index, Dictionary<string, YamlNode> anchors, string source)
    {
        var current = events[index++];
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw YamlException.At(ErrorMessages.NestingLimitExceeded, ContentFormatErrorCode.LimitExceeded, current.Line, current.Column, source);
        }

        switch (current.Kind)
        {
            case YamlEventKind.Scalar:
            {
                var scalar = CreateScalar(current);
                Register(anchors, current.Anchor, scalar);
                return scalar;
            }

            case YamlEventKind.Alias:
            {
                if (current.Value is null || !anchors.TryGetValue(current.Value, out var target))
                {
                    throw YamlException.At(string.Format(CultureInfo.CurrentCulture, ErrorMessages.AliasAnchorMissing, current.Value), ContentFormatErrorCode.InvalidStructure, current.Line, current.Column, source);
                }

                return target;
            }

            case YamlEventKind.SequenceStart:
            {
                var sequence = new YamlSequence
                {
                    Style = current.CollectionStyle,
                    Anchor = current.Anchor,
                    Tag = current.Tag
                };
                Register(anchors, current.Anchor, sequence);

                while (events[index].Kind != YamlEventKind.SequenceEnd)
                {
                    sequence.Add(ComposeNode(events, ref index, anchors, source));
                }

                index++; // SequenceEnd
                return sequence;
            }

            case YamlEventKind.MappingStart:
            {
                var mapping = new YamlMapping
                {
                    Style = current.CollectionStyle,
                    Anchor = current.Anchor,
                    Tag = current.Tag
                };
                Register(anchors, current.Anchor, mapping);

                while (events[index].Kind != YamlEventKind.MappingEnd)
                {
                    var key = ComposeNode(events, ref index, anchors, source);
                    var value = ComposeNode(events, ref index, anchors, source);
                    mapping.Add(key, value);
                }

                index++; // MappingEnd
                return mapping;
            }

            default:
                throw YamlException.At(string.Format(CultureInfo.CurrentCulture, ErrorMessages.NodeEventUnexpected, current.Kind), ContentFormatErrorCode.InvalidStructure, current.Line, current.Column, source);
        }
    }

    private static YamlScalar CreateScalar(YamlEvent current)
    {
        // A missing value is the implicit null scalar.
        if (current.Value is null)
        {
            return new YamlScalar(string.Empty, YamlScalarKind.Null, YamlScalarStyle.Plain)
            {
                Anchor = current.Anchor,
                Tag = current.Tag
            };
        }

        // Quoted and block scalars are strings; plain scalars resolve through the core schema.
        // Explicit yaml.org core tags override both.
        var kind = current.ScalarStyle == YamlScalarStyle.Plain
            ? YamlCoreSchema.Resolve(current.Value)
            : YamlScalarKind.String;

        kind = current.Tag?.Value switch
        {
            "tag:yaml.org,2002:str" => YamlScalarKind.String,
            "tag:yaml.org,2002:null" => YamlScalarKind.Null,
            "tag:yaml.org,2002:bool" => YamlScalarKind.Boolean,
            "tag:yaml.org,2002:int" => YamlScalarKind.Integer,
            "tag:yaml.org,2002:float" => YamlScalarKind.Float,
            _ => kind
        };

        return new YamlScalar(current.Value, kind, current.ScalarStyle)
        {
            Anchor = current.Anchor,
            Tag = current.Tag
        };
    }

    private static void Register(Dictionary<string, YamlNode> anchors, YamlAnchorName? anchor, YamlNode node)
    {
        if (anchor is { } name)
        {
            anchors[name.Value] = node;
        }
    }

    private static void Expect(IReadOnlyList<YamlEvent> events, ref int index, string source, YamlEventKind kind)
    {
        var current = events[index++];
        if (current.Kind != kind)
        {
            throw YamlException.At(string.Format(CultureInfo.CurrentCulture, ErrorMessages.EventKindUnexpected, kind, current.Kind), ContentFormatErrorCode.InvalidStructure, current.Line, current.Column, source);
        }
    }
}
