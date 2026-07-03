using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Emits the document model as deterministic YAML text: block styles by default, flow where a
/// collection asks for it or is empty, plain scalars where safe, and quoting or literal blocks where
/// content demands it. Nodes that occur more than once emit an anchor on first use and aliases after.
/// </summary>
internal sealed class YamlNodeEmitter
{
    private readonly StringBuilder _output = new();
    private readonly YamlWriterOptions _options;
    private readonly Dictionary<YamlNode, string> _sharedAnchors = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<YamlNode> _emitted = new(ReferenceEqualityComparer.Instance);
    private int _autoAnchor;

    private YamlNodeEmitter(YamlWriterOptions options)
    {
        _options = options;
    }

    internal static string Emit(YamlStream stream, YamlWriterOptions options)
    {
        var emitter = new YamlNodeEmitter(options);
        emitter.WriteStream(stream);
        return emitter._output.ToString();
    }

    private void WriteStream(YamlStream stream)
    {
        for (var index = 0; index < stream.Documents.Count; index++)
        {
            var document = stream.Documents[index];

            if (index > 0 || document.IsExplicit || stream.Documents.Count > 1)
            {
                _output.Append("---");
                if (document.Root is YamlScalar || document.Root is null)
                {
                    // Scalar roots share the marker line; collections start on the next line.
                }

                if (document.Root is null)
                {
                    _output.Append('\n');
                    continue;
                }

                if (document.Root is YamlScalar)
                {
                    _output.Append(' ');
                    WriteNode(document.Root, indent: 0, isMappingValuePosition: false);
                    EnsureTrailingBreak();
                    continue;
                }

                _output.Append('\n');
            }
            else if (document.Root is null)
            {
                continue;
            }

            AssignSharedAnchors(document.Root);
            WriteNode(document.Root, indent: 0, isMappingValuePosition: false);
            EnsureTrailingBreak();
        }
    }

    // ------------------------------------------------------------- structure

    private void WriteNode(YamlNode node, int indent, bool isMappingValuePosition, bool skipPrefix = false)
    {
        // A caller that already wrote the node's anchor inline owns the occurrence bookkeeping.
        if (!skipPrefix && TryWriteAlias(node))
        {
            EnsureTrailingBreak();
            return;
        }

        switch (node)
        {
            case YamlScalar scalar:
                WriteScalarNode(scalar, indent);
                EnsureTrailingBreak();
                break;

            case YamlSequence sequence when sequence.Style == YamlCollectionStyle.Flow || sequence.Count == 0:
                WritePrefix(sequence);
                WriteFlowSequence(sequence);
                EnsureTrailingBreak();
                break;

            case YamlMapping mapping when mapping.Style == YamlCollectionStyle.Flow || mapping.Count == 0:
                WritePrefix(mapping);
                WriteFlowMapping(mapping);
                EnsureTrailingBreak();
                break;

            case YamlSequence sequence:
                WriteBlockSequence(sequence, indent, skipPrefix);
                break;

            case YamlMapping mapping:
                WriteBlockMapping(mapping, indent, skipPrefix);
                break;

            default:
                throw new InvalidOperationException($"Unknown node type '{node.GetType().Name}'.");
        }

        _ = isMappingValuePosition;
    }

    private void WriteBlockSequence(YamlSequence sequence, int indent, bool skipPrefix = false, bool inlineFirstEntry = false)
    {
        var prefix = skipPrefix ? string.Empty : PrefixText(sequence);
        if (prefix.Length > 0)
        {
            _output.Append(prefix.TrimEnd()).Append('\n');
        }

        var first = true;
        foreach (var item in sequence.Items)
        {
            if (!(inlineFirstEntry && first))
            {
                Indent(indent);
            }

            first = false;
            _output.Append('-');

            if (TryWriteAliasInline(item, out var alias))
            {
                _output.Append(' ').Append(alias).Append('\n');
                continue;
            }

            switch (item)
            {
                case YamlScalar scalar:
                    _output.Append(' ');
                    WriteScalarNode(scalar, indent + _options.IndentSize);
                    EnsureTrailingBreak();
                    break;

                case YamlSequence nested when nested.Style == YamlCollectionStyle.Flow || nested.Count == 0:
                    _output.Append(' ');
                    WritePrefix(nested);
                    WriteFlowSequence(nested);
                    _output.Append('\n');
                    break;

                case YamlMapping nested when nested.Style == YamlCollectionStyle.Flow || nested.Count == 0:
                    _output.Append(' ');
                    WritePrefix(nested);
                    WriteFlowMapping(nested);
                    _output.Append('\n');
                    break;

                case YamlSequence nested:
                {
                    var itemPrefix = PrefixText(item);
                    if (itemPrefix.Length > 0)
                    {
                        // Anchored or tagged nested collections keep the properties on the dash line.
                        _output.Append(' ').Append(itemPrefix.TrimEnd()).Append('\n');
                        WriteBlockSequence(nested, indent + _options.IndentSize, skipPrefix: true);
                    }
                    else
                    {
                        _output.Append(' ');
                        WriteBlockSequence(nested, indent + _options.IndentSize, skipPrefix: true, inlineFirstEntry: true);
                    }

                    break;
                }

                case YamlMapping nested:
                {
                    var itemPrefix = PrefixText(item);
                    if (itemPrefix.Length > 0)
                    {
                        _output.Append(' ').Append(itemPrefix.TrimEnd()).Append('\n');
                        WriteBlockMapping(nested, indent + _options.IndentSize, skipPrefix: true);
                    }
                    else
                    {
                        _output.Append(' ');
                        WriteBlockMapping(nested, indent + _options.IndentSize, skipPrefix: true, inlineFirstEntry: true);
                    }

                    break;
                }
            }
        }
    }

    private void WriteBlockMapping(YamlMapping mapping, int indent, bool skipPrefix = false, bool inlineFirstEntry = false)
    {
        var prefix = skipPrefix ? string.Empty : PrefixText(mapping);
        if (prefix.Length > 0)
        {
            _output.Append(prefix.TrimEnd()).Append('\n');
        }

        var first = true;
        foreach (var entry in mapping.Entries)
        {
            if (!(inlineFirstEntry && first))
            {
                Indent(indent);
            }

            first = false;

            if (entry.Key is YamlScalar keyScalar && !keyScalar.Value.Contains('\n') && _sharedAnchors.ContainsKey(entry.Key) == false)
            {
                WriteScalarNode(keyScalar, indent, forceSingleLine: true);
            }
            else
            {
                // Complex keys use the explicit form.
                _output.Append("? ");
                WriteNode(entry.Key, indent + _options.IndentSize, isMappingValuePosition: false);
                TrimTrailingBreak();
                _output.Append('\n');
                Indent(indent);
            }

            _output.Append(':');

            if (TryWriteAliasInline(entry.Value, out var alias))
            {
                _output.Append(' ').Append(alias).Append('\n');
                continue;
            }

            switch (entry.Value)
            {
                case YamlScalar scalar:
                    _output.Append(' ');
                    WriteScalarNode(scalar, indent + _options.IndentSize);
                    EnsureTrailingBreak();
                    break;

                case YamlSequence nested when nested.Style == YamlCollectionStyle.Flow || nested.Count == 0:
                    _output.Append(' ');
                    WritePrefix(nested);
                    WriteFlowSequence(nested);
                    _output.Append('\n');
                    break;

                case YamlMapping nested when nested.Style == YamlCollectionStyle.Flow || nested.Count == 0:
                    _output.Append(' ');
                    WritePrefix(nested);
                    WriteFlowMapping(nested);
                    _output.Append('\n');
                    break;

                default:
                {
                    var valuePrefix = PrefixText(entry.Value);
                    if (valuePrefix.Length > 0)
                    {
                        _output.Append(' ').Append(valuePrefix.TrimEnd());
                    }

                    _output.Append('\n');
                    WriteNode(entry.Value, indent + _options.IndentSize, isMappingValuePosition: true, skipPrefix: true);
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ flow

    private void WriteFlowNode(YamlNode node)
    {
        if (TryWriteAliasInline(node, out var alias))
        {
            _output.Append(alias);
            return;
        }

        switch (node)
        {
            case YamlScalar scalar:
                WritePrefix(scalar);
                _output.Append(FormatFlowScalar(scalar));
                break;

            case YamlSequence sequence:
                WritePrefix(sequence);
                WriteFlowSequence(sequence);
                break;

            case YamlMapping mapping:
                WritePrefix(mapping);
                WriteFlowMapping(mapping);
                break;
        }
    }

    private void WriteFlowSequence(YamlSequence sequence)
    {
        _output.Append('[');
        for (var index = 0; index < sequence.Count; index++)
        {
            if (index > 0)
            {
                _output.Append(", ");
            }

            WriteFlowNode(sequence[index]);
        }

        _output.Append(']');
    }

    private void WriteFlowMapping(YamlMapping mapping)
    {
        _output.Append('{');
        for (var index = 0; index < mapping.Count; index++)
        {
            if (index > 0)
            {
                _output.Append(", ");
            }

            var entry = mapping.Entries[index];
            WriteFlowNode(entry.Key);
            _output.Append(": ");
            WriteFlowNode(entry.Value);
        }

        _output.Append('}');
    }

    // --------------------------------------------------------------- scalars

    private void WriteScalarNode(YamlScalar scalar, int indent, bool forceSingleLine = false)
    {
        WritePrefix(scalar);

        var value = scalar.Value;

        if (scalar.Kind != YamlScalarKind.String)
        {
            _output.Append(FormatNonString(scalar));
            return;
        }

        if (!forceSingleLine && value.Contains('\n') && IsLiteralSafe(value))
        {
            WriteLiteralBlock(value, indent);
            return;
        }

        if (IsPlainSafe(value) && YamlCoreSchema.Resolve(value) == YamlScalarKind.String)
        {
            _output.Append(value);
            return;
        }

        _output.Append(QuoteDouble(value));
    }

    private string FormatFlowScalar(YamlScalar scalar)
    {
        if (scalar.Kind != YamlScalarKind.String)
        {
            return FormatNonString(scalar);
        }

        var value = scalar.Value;
        if (IsPlainSafe(value) && !value.Contains('\n') && YamlCoreSchema.Resolve(value) == YamlScalarKind.String
            && value.IndexOfAny([',', '[', ']', '{', '}']) < 0)
        {
            return value;
        }

        return QuoteDouble(value);
    }

    private static string FormatNonString(YamlScalar scalar) => scalar.Kind switch
    {
        YamlScalarKind.Null => "null",
        _ => scalar.Value
    };

    private void WriteLiteralBlock(string value, int indent)
    {
        // Choose the chomping indicator that reproduces the value's trailing breaks.
        string body;
        string indicator;
        if (value.EndsWith("\n\n", StringComparison.Ordinal))
        {
            indicator = "|+";
            body = value;
        }
        else if (value.EndsWith('\n'))
        {
            indicator = "|";
            body = value[..^1];
        }
        else
        {
            indicator = "|-";
            body = value;
        }

        _output.Append(indicator).Append('\n');
        foreach (var line in body.Split('\n'))
        {
            if (line.Length > 0)
            {
                Indent(indent);
                _output.Append(line);
            }

            _output.Append('\n');
        }

        TrimTrailingBreak();
    }

    private static bool IsLiteralSafe(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character) && character != '\n')
            {
                return false;
            }
        }

        // Leading spaces on the first line would be misread as extra block indentation.
        return !value.StartsWith(' ') && !value.StartsWith("\n", StringComparison.Ordinal);
    }

    private static bool IsPlainSafe(string value)
    {
        if (value.Length == 0 || value != value.Trim() || value.Contains('\n') || value.Contains('\t'))
        {
            return false;
        }

        var first = value[0];
        if (first is '-' or '?' or ':' && (value.Length == 1 || value[1] == ' '))
        {
            return false;
        }

        if (first is '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`' or ',' or '[' or ']' or '{' or '}')
        {
            return false;
        }

        if (value is "---" or "...")
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character))
            {
                return false;
            }

            if (character == ':' && (index + 1 == value.Length || value[index + 1] == ' '))
            {
                return false;
            }

            if (character == '#' && index > 0 && value[index - 1] == ' ')
            {
                return false;
            }
        }

        return true;
    }

    private static string QuoteDouble(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\0':
                    builder.Append("\\0");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    // -------------------------------------------------------- anchors and tags

    /// <summary>Walks the graph and assigns anchor names to nodes that occur more than once.</summary>
    private void AssignSharedAnchors(YamlNode root)
    {
        var seen = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        Visit(root);

        void Visit(YamlNode node)
        {
            if (!seen.Add(node))
            {
                if (!_sharedAnchors.ContainsKey(node))
                {
                    _sharedAnchors[node] = node.Anchor ?? $"a{++_autoAnchor}";
                }

                return;
            }

            switch (node)
            {
                case YamlSequence sequence:
                    foreach (var item in sequence.Items)
                    {
                        Visit(item);
                    }

                    break;

                case YamlMapping mapping:
                    foreach (var entry in mapping.Entries)
                    {
                        Visit(entry.Key);
                        Visit(entry.Value);
                    }

                    break;
            }
        }
    }

    private bool TryWriteAlias(YamlNode node)
    {
        if (TryWriteAliasInline(node, out var alias))
        {
            _output.Append(alias);
            return true;
        }

        return false;
    }

    private bool TryWriteAliasInline(YamlNode node, out string alias)
    {
        if (_sharedAnchors.TryGetValue(node, out var name) && !_emitted.Add(node))
        {
            alias = "*" + name;
            return true;
        }

        alias = string.Empty;
        return false;
    }

    private void WritePrefix(YamlNode node)
    {
        if (_sharedAnchors.TryGetValue(node, out var name))
        {
            _emitted.Add(node);
            _output.Append('&').Append(name).Append(' ');
        }

        if (node.Tag is { } tag && tag != "!")
        {
            _output.Append(FormatTag(tag)).Append(' ');
        }
    }

    private string PrefixText(YamlNode node)
    {
        var start = _output.Length;
        WritePrefix(node);
        var text = _output.ToString(start, _output.Length - start);
        _output.Length = start;
        return text;
    }

    private static string FormatTag(string tag)
    {
        if (tag.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
        {
            return "!!" + tag["tag:yaml.org,2002:".Length..];
        }

        if (tag.StartsWith('!'))
        {
            return tag;
        }

        return "!<" + tag + ">";
    }

    // ----------------------------------------------------------------- output

    private void Indent(int indent) => _output.Append(' ', indent);

    private void EnsureTrailingBreak()
    {
        if (_output.Length > 0 && _output[^1] != '\n')
        {
            _output.Append('\n');
        }
    }

    private void TrimTrailingBreak()
    {
        while (_output.Length > 0 && _output[^1] == '\n')
        {
            _output.Length--;
        }
    }
}
