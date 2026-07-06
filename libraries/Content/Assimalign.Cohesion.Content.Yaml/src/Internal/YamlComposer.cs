using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Composes the parse-event stream into the document model, resolving aliases to shared node
/// instances. Anchors are registered before children are composed so self-referential structures
/// compose without special cases.
/// </summary>
internal static class YamlComposer
{
    internal static YamlStream Compose(IReadOnlyList<YamlEvent> events)
    {
        var stream = new YamlStream();
        var index = 0;

        Expect(events, ref index, YamlEventKind.StreamStart);

        while (events[index].Kind == YamlEventKind.DocumentStart)
        {
            var start = events[index++];
            var document = new YamlDocument { IsExplicit = start.IsExplicit };
            var anchors = new Dictionary<string, YamlNode>();

            if (events[index].Kind != YamlEventKind.DocumentEnd)
            {
                document.Root = ComposeNode(events, ref index, anchors);
            }

            Expect(events, ref index, YamlEventKind.DocumentEnd);
            stream.Documents.Add(document);
        }

        Expect(events, ref index, YamlEventKind.StreamEnd);
        return stream;
    }

    private static YamlNode ComposeNode(IReadOnlyList<YamlEvent> events, ref int index, Dictionary<string, YamlNode> anchors)
    {
        var current = events[index++];

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
                    throw new YamlException($"Alias '*{current.Value}' refers to an undefined anchor.", current.Line, current.Column);
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
                    sequence.Add(ComposeNode(events, ref index, anchors));
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
                    var key = ComposeNode(events, ref index, anchors);
                    var value = ComposeNode(events, ref index, anchors);
                    mapping.Add(key, value);
                }

                index++; // MappingEnd
                return mapping;
            }

            default:
                throw new YamlException($"Unexpected '{current.Kind}' event while composing a node.", current.Line, current.Column);
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

        kind = current.Tag switch
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

    private static void Register(Dictionary<string, YamlNode> anchors, string? anchor, YamlNode node)
    {
        if (anchor is not null)
        {
            anchors[anchor] = node;
        }
    }

    private static void Expect(IReadOnlyList<YamlEvent> events, ref int index, YamlEventKind kind)
    {
        var current = events[index++];
        if (current.Kind != kind)
        {
            throw new YamlException($"Expected a '{kind}' event but found '{current.Kind}'.", current.Line, current.Column);
        }
    }
}
