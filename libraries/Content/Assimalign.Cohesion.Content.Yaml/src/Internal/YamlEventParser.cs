using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Parses YAML 1.2 text into the event stream by recursive descent over a normalized character
/// cursor. Line breaks are normalized to <c>\n</c> before parsing; positions are one-based.
/// </summary>
internal sealed class YamlEventParser
{
    private readonly string _text;
    private readonly List<YamlEvent> _events = [];
    private Dictionary<string, string>? _tagHandles;
    private int _pos;
    private int _line = 1;
    private int _lineStart;

    private YamlEventParser(string text)
    {
        _text = text;
    }

    internal static List<YamlEvent> Parse(string text)
    {
        var parser = new YamlEventParser(Normalize(text));
        parser.ParseStream();
        return parser._events;
    }

    private static string Normalize(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // ---------------------------------------------------------------- stream

    private void ParseStream()
    {
        Emit(new YamlEvent(YamlEventKind.StreamStart, _line, Column));

        while (true)
        {
            SkipBlankAndCommentLines();
            if (AtEnd)
            {
                break;
            }

            ParseDocument();
        }

        Emit(new YamlEvent(YamlEventKind.StreamEnd, _line, Column));
    }

    private void ParseDocument()
    {
        _tagHandles = null;
        var sawYamlDirective = false;
        var hasDirectives = false;

        while (!AtEnd && Column == 0 && Peek() == '%')
        {
            ParseDirective(ref sawYamlDirective);
            hasDirectives = true;
            SkipBlankAndCommentLines();
        }

        var explicitStart = false;
        if (AtDocumentStartMarker())
        {
            Advance(3);
            explicitStart = true;
        }
        else if (hasDirectives)
        {
            throw Error("Directives must be followed by a '---' document start marker.");
        }

        Emit(new YamlEvent(YamlEventKind.DocumentStart, _line, Column, isExplicit: explicitStart));

        // Locate the document's root content, which may sit on the '---' line itself.
        SkipSpacesAndTabs();
        var state = Save();
        SkipBlankAndCommentLines();
        if (AtEnd || AtDocumentStartMarker() || AtDocumentEndMarker())
        {
            Restore(state);
        }
        else
        {
            ParseBlockNode(minIndent: -1);
        }

        SkipBlankAndCommentLines();

        var explicitEnd = false;
        if (AtDocumentEndMarker())
        {
            Advance(3);
            SkipRestOfLine();
            explicitEnd = true;
        }

        Emit(new YamlEvent(YamlEventKind.DocumentEnd, _line, Column, isExplicit: explicitEnd));
    }

    private void ParseDirective(ref bool sawYamlDirective)
    {
        Advance(); // '%'
        var name = ReadWhile(static c => c is not ' ' and not '\t' and not '\n');
        SkipSpacesAndTabs();

        if (name == "YAML")
        {
            if (sawYamlDirective)
            {
                throw Error("Only one %YAML directive is allowed per document.");
            }

            sawYamlDirective = true;
            var version = ReadWhile(static c => c is not ' ' and not '\t' and not '\n' and not '#');
            if (version is not ("1.1" or "1.2"))
            {
                throw Error($"Unsupported YAML version '{version}'; this parser supports the 1.2 line.");
            }
        }
        else if (name == "TAG")
        {
            var handle = ReadWhile(static c => c is not ' ' and not '\t' and not '\n');
            SkipSpacesAndTabs();
            var prefix = ReadWhile(static c => c is not ' ' and not '\t' and not '\n' and not '#');
            if (handle.Length == 0 || prefix.Length == 0)
            {
                throw Error("A %TAG directive requires a handle and a prefix.");
            }

            _tagHandles ??= [];
            _tagHandles[handle] = prefix;
        }

        // Unknown directives are ignored per the specification.
        SkipRestOfLine();
    }

    // ------------------------------------------------------------ block nodes

    /// <summary>Parses one node in block context whose content sits at a column greater than <paramref name="minIndent"/>.</summary>
    private void ParseBlockNode(int minIndent)
    {
        SkipBlankAndCommentLines();
        var (anchor, tag, hadProperties) = ParseNodeProperties(minIndent);

        if (AtEnd || AtDocumentStartMarker() || AtDocumentEndMarker() || Column <= minIndent)
        {
            EmitNullScalar(anchor, tag);
            return;
        }

        var column = Column;
        var c = Peek();

        if (c == '*')
        {
            var aliasLine = _line;
            var aliasColumn = Column;
            Advance();
            var name = ReadAnchorName();
            SkipSpacesAndTabs();
            if (Peek() == ':' && IsSpaceLikeOrEnd(Peek(1)))
            {
                var startIndex = _events.Count;
                Emit(new YamlEvent(YamlEventKind.Alias, aliasLine, aliasColumn + 1, value: name));
                Advance();
                BeginBlockMapping(column, startIndex);
                return;
            }

            Emit(new YamlEvent(YamlEventKind.Alias, aliasLine, aliasColumn + 1, value: name));
            return;
        }

        if (c == '-' && IsSpaceLikeOrEnd(Peek(1)))
        {
            ParseBlockSequence(column, anchor, tag);
            return;
        }

        if (c == '?' && IsSpaceLikeOrEnd(Peek(1)))
        {
            Emit(new YamlEvent(YamlEventKind.MappingStart, _line, column + 1, anchor: anchor, tag: tag));
            ParseBlockMappingEntries(column, firstImplicitKeyPending: false);
            return;
        }

        if (c is '|' or '>')
        {
            ParseBlockScalar(minIndent, folded: c == '>', anchor, tag);
            return;
        }

        if (c is '[' or '{')
        {
            var startIndex = _events.Count;
            ParseFlowNode(anchor, tag);
            SkipSpacesAndTabs();
            if (Peek() == ':' && IsSpaceLikeOrEnd(Peek(1)))
            {
                Advance();
                BeginBlockMapping(column, startIndex);
            }

            return;
        }

        if (c is '"' or '\'')
        {
            var line = _line;
            var startColumn = Column;
            var singleLine = true;
            var value = c == '"'
                ? ScanDoubleQuoted(minIndent, ref singleLine)
                : ScanSingleQuoted(minIndent, ref singleLine);
            var style = c == '"' ? YamlScalarStyle.DoubleQuoted : YamlScalarStyle.SingleQuoted;

            SkipSpacesAndTabs();
            if (Peek() == ':' && IsSpaceLikeOrEnd(Peek(1)))
            {
                if (!singleLine)
                {
                    throw Error("A mapping key must not span multiple lines.");
                }

                var startIndex = _events.Count;
                Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: value, anchor: anchor, tag: tag, scalarStyle: style));
                Advance();
                BeginBlockMapping(column, startIndex);
                return;
            }

            Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: value, anchor: anchor, tag: tag, scalarStyle: style));
            return;
        }

        // Plain scalar: scan the first line, then decide between a mapping key and a (possibly
        // multi-line) scalar value.
        {
            var line = _line;
            var startColumn = Column;
            var (firstLine, endedAtColon) = ScanPlainFirstLine(inFlow: false);
            if (firstLine.Length == 0 && !endedAtColon)
            {
                throw Error($"Unexpected character '{Peek()}'.");
            }

            if (endedAtColon)
            {
                var startIndex = _events.Count;
                Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: firstLine, anchor: anchor, tag: tag));
                Advance(); // ':'
                BeginBlockMapping(column, startIndex);
                return;
            }

            var value = ContinuePlainMultiline(firstLine, minIndent);
            Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: value, anchor: anchor, tag: tag));
            _ = hadProperties;
        }
    }

    /// <summary>Wraps an already-emitted key node into a mapping and parses the remaining entries.</summary>
    private void BeginBlockMapping(int column, int firstKeyStartIndex)
    {
        var keyEvent = _events[firstKeyStartIndex];
        _events.Insert(firstKeyStartIndex, new YamlEvent(YamlEventKind.MappingStart, keyEvent.Line, column + 1));
        ParseValueAfterColon(column);
        ParseBlockMappingEntries(column, firstImplicitKeyPending: true);
    }

    private void ParseBlockMappingEntries(int column, bool firstImplicitKeyPending)
    {
        // When the first implicit entry was already handled by BeginBlockMapping, this loop parses
        // the remaining entries; otherwise it owns the whole mapping (explicit '?' form).
        while (true)
        {
            var state = Save();
            SkipBlankAndCommentLines();

            if (AtEnd || AtDocumentStartMarker() || AtDocumentEndMarker() || Column != column)
            {
                Restore(state);
                break;
            }

            if (Peek() == '?' && IsSpaceLikeOrEnd(Peek(1)))
            {
                Advance();
                ParseBlockNode(column);

                var valueState = Save();
                SkipBlankAndCommentLines();
                if (!AtEnd && Column == column && Peek() == ':' && IsSpaceLikeOrEnd(Peek(1)))
                {
                    Advance();
                    ParseValueAfterColon(column);
                }
                else
                {
                    Restore(valueState);
                    EmitNullScalar(null, null);
                }

                continue;
            }

            if (!TryParseImplicitKey(column))
            {
                Restore(state);
                break;
            }

            ParseValueAfterColon(column);
        }

        _ = firstImplicitKeyPending;
        Emit(new YamlEvent(YamlEventKind.MappingEnd, _line, Column + 1));
    }

    /// <summary>Parses a single-line key followed by ':'. Returns false when the content is not a key.</summary>
    private bool TryParseImplicitKey(int column)
    {
        var (anchor, tag, _) = ParseInlineProperties();
        var c = Peek();
        var line = _line;
        var startColumn = Column;

        if (c == '*')
        {
            Advance();
            var name = ReadAnchorName();
            SkipSpacesAndTabs();
            if (Peek() != ':' || !IsSpaceLikeOrEnd(Peek(1)))
            {
                return false;
            }

            Emit(new YamlEvent(YamlEventKind.Alias, line, startColumn + 1, value: name));
            Advance();
            return true;
        }

        if (c is '[' or '{')
        {
            ParseFlowNode(anchor, tag);
            SkipSpacesAndTabs();
            if (Peek() != ':' || !IsSpaceLikeOrEnd(Peek(1)))
            {
                throw Error("Expected ':' after a flow-collection mapping key.");
            }

            Advance();
            return true;
        }

        if (c is '"' or '\'')
        {
            var singleLine = true;
            var value = c == '"'
                ? ScanDoubleQuoted(column, ref singleLine)
                : ScanSingleQuoted(column, ref singleLine);
            if (!singleLine)
            {
                throw Error("A mapping key must not span multiple lines.");
            }

            SkipSpacesAndTabs();
            if (Peek() != ':' || !IsSpaceLikeOrEnd(Peek(1)))
            {
                throw Error("Expected ':' after a quoted mapping key.");
            }

            Emit(new YamlEvent(
                YamlEventKind.Scalar,
                line,
                startColumn + 1,
                value: value,
                anchor: anchor,
                tag: tag,
                scalarStyle: c == '"' ? YamlScalarStyle.DoubleQuoted : YamlScalarStyle.SingleQuoted));
            Advance();
            return true;
        }

        var (text, endedAtColon) = ScanPlainFirstLine(inFlow: false);
        if (!endedAtColon)
        {
            return false;
        }

        Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: text, anchor: anchor, tag: tag));
        Advance();
        return true;
    }

    private void ParseValueAfterColon(int keyColumn)
    {
        SkipSpacesAndTabs();

        if (AtEnd || Peek() == '\n' || Peek() == '#')
        {
            SkipRestOfLine();

            var state = Save();
            SkipBlankAndCommentLines();

            if (AtEnd || AtDocumentStartMarker() || AtDocumentEndMarker())
            {
                Restore(state);
                EmitNullScalar(null, null);
                return;
            }

            var column = Column;
            // A block sequence may sit at the same column as its parent mapping key.
            if (column > keyColumn || (column == keyColumn && Peek() == '-' && IsSpaceLikeOrEnd(Peek(1))))
            {
                Restore(state);
                ParseBlockNode(column == keyColumn ? keyColumn - 1 : keyColumn);
                return;
            }

            Restore(state);
            EmitNullScalar(null, null);
            return;
        }

        if (Peek() == '-' && IsSpaceLikeOrEnd(Peek(1)))
        {
            throw Error("A block sequence entry is not allowed on the same line as a mapping key.");
        }

        ParseInlineValue(keyColumn);
    }

    /// <summary>Parses a node that begins on the current line (a value after ':' or '-').</summary>
    private void ParseInlineValue(int minIndent)
    {
        var (anchor, tag, hadProperties) = ParseInlineProperties();

        if (AtEnd || Peek() == '\n' || Peek() == '#')
        {
            if (hadProperties)
            {
                // Properties on their own line apply to the node on the following lines.
                var state = Save();
                SkipBlankAndCommentLines();
                if (!AtEnd && !AtDocumentStartMarker() && !AtDocumentEndMarker() && Column > minIndent)
                {
                    ParsePropertiedBlockNode(minIndent, anchor, tag);
                    return;
                }

                Restore(state);
            }

            EmitNullScalar(anchor, tag);
            return;
        }

        var c = Peek();
        var line = _line;
        var startColumn = Column;

        if (c == '*')
        {
            Advance();
            var name = ReadAnchorName();
            Emit(new YamlEvent(YamlEventKind.Alias, line, startColumn + 1, value: name));
            CheckNoTrailingContent();
            return;
        }

        if (c == '-' && IsSpaceLikeOrEnd(Peek(1)))
        {
            // Only sequence-entry values reach here (colon values are rejected earlier).
            ParseBlockSequence(startColumn, anchor, tag);
            return;
        }

        if (c == '?' && IsSpaceLikeOrEnd(Peek(1)))
        {
            Emit(new YamlEvent(YamlEventKind.MappingStart, line, startColumn + 1, anchor: anchor, tag: tag));
            ParseBlockMappingEntries(startColumn, firstImplicitKeyPending: false);
            return;
        }

        if (c is '|' or '>')
        {
            ParseBlockScalar(minIndent, folded: c == '>', anchor, tag);
            return;
        }

        if (c is '[' or '{')
        {
            var startIndex = _events.Count;
            ParseFlowNode(anchor, tag);
            SkipSpacesAndTabs();
            if (Peek() == ':' && IsSpaceLikeOrEnd(Peek(1)))
            {
                Advance();
                BeginBlockMapping(startColumn, startIndex);
            }

            return;
        }

        if (c is '"' or '\'')
        {
            var singleLine = true;
            var value = c == '"'
                ? ScanDoubleQuoted(minIndent, ref singleLine)
                : ScanSingleQuoted(minIndent, ref singleLine);
            var style = c == '"' ? YamlScalarStyle.DoubleQuoted : YamlScalarStyle.SingleQuoted;

            SkipSpacesAndTabs();
            if (Peek() == ':' && IsSpaceLikeOrEnd(Peek(1)))
            {
                if (!singleLine)
                {
                    throw Error("A mapping key must not span multiple lines.");
                }

                var startIndex = _events.Count;
                Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: value, anchor: anchor, tag: tag, scalarStyle: style));
                Advance();
                BeginBlockMapping(startColumn, startIndex);
                return;
            }

            Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: value, anchor: anchor, tag: tag, scalarStyle: style));
            return;
        }

        {
            var (firstLine, endedAtColon) = ScanPlainFirstLine(inFlow: false);
            if (endedAtColon)
            {
                var startIndex = _events.Count;
                Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: firstLine, anchor: anchor, tag: tag));
                Advance();
                BeginBlockMapping(startColumn, startIndex);
                return;
            }

            if (firstLine.Length == 0)
            {
                EmitNullScalar(anchor, tag);
                return;
            }

            var value = ContinuePlainMultiline(firstLine, minIndent);
            Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: value, anchor: anchor, tag: tag));
        }
    }

    /// <summary>Parses a block node whose anchor/tag properties were consumed on an earlier line.</summary>
    private void ParsePropertiedBlockNode(int minIndent, string? anchor, string? tag)
    {
        var column = Column;
        var c = Peek();

        if (c == '-' && IsSpaceLikeOrEnd(Peek(1)))
        {
            ParseBlockSequence(column, anchor, tag);
            return;
        }

        if (c == '?' && IsSpaceLikeOrEnd(Peek(1)))
        {
            Emit(new YamlEvent(YamlEventKind.MappingStart, _line, column + 1, anchor: anchor, tag: tag));
            ParseBlockMappingEntries(column, firstImplicitKeyPending: false);
            return;
        }

        // Delegate the remaining shapes to the inline-value parser at this position.
        var startIndex = _events.Count;
        ParseInlineValue(minIndent);

        // Attach the carried properties to the first node event produced.
        if (anchor is not null || tag is not null)
        {
            var first = _events[startIndex];
            _events[startIndex] = new YamlEvent(
                first.Kind,
                first.Line,
                first.Column,
                first.Value,
                anchor ?? first.Anchor,
                tag ?? first.Tag,
                first.ScalarStyle,
                first.CollectionStyle,
                first.IsExplicit);
        }
    }

    private void ParseBlockSequence(int column, string? anchor, string? tag)
    {
        Emit(new YamlEvent(YamlEventKind.SequenceStart, _line, column + 1, anchor: anchor, tag: tag));

        while (true)
        {
            var state = Save();
            SkipBlankAndCommentLines();

            if (AtEnd || AtDocumentStartMarker() || AtDocumentEndMarker() || Column != column
                || Peek() != '-' || !IsSpaceLikeOrEnd(Peek(1)))
            {
                Restore(state);
                break;
            }

            Advance(); // '-'
            SkipSpacesAndTabs();

            if (AtEnd || Peek() == '\n' || Peek() == '#')
            {
                SkipRestOfLine();
                var entryState = Save();
                SkipBlankAndCommentLines();
                if (!AtEnd && !AtDocumentStartMarker() && !AtDocumentEndMarker() && Column > column)
                {
                    Restore(entryState);
                    ParseBlockNode(column);
                }
                else
                {
                    Restore(entryState);
                    EmitNullScalar(null, null);
                }
            }
            else
            {
                ParseInlineValue(column);
            }
        }

        Emit(new YamlEvent(YamlEventKind.SequenceEnd, _line, Column + 1));
    }

    // ------------------------------------------------------------ flow nodes

    private void ParseFlowNode(string? anchor, string? tag)
    {
        var c = Peek();
        if (c == '[')
        {
            ParseFlowSequence(anchor, tag);
        }
        else if (c == '{')
        {
            ParseFlowMapping(anchor, tag);
        }
        else
        {
            ParseFlowScalarOrAlias(anchor, tag);
        }
    }

    private void ParseFlowSequence(string? anchor, string? tag)
    {
        Emit(new YamlEvent(YamlEventKind.SequenceStart, _line, Column + 1, anchor: anchor, tag: tag, collectionStyle: YamlCollectionStyle.Flow));
        Advance(); // '['

        while (true)
        {
            SkipFlowSeparation();
            if (AtEnd)
            {
                throw Error("Unterminated flow sequence.");
            }

            if (Peek() == ']')
            {
                Advance();
                break;
            }

            ParseFlowEntry();

            SkipFlowSeparation();
            if (Peek() == ',')
            {
                Advance();
                continue;
            }

            if (Peek() == ']')
            {
                Advance();
                break;
            }

            throw Error("Expected ',' or ']' in a flow sequence.");
        }

        Emit(new YamlEvent(YamlEventKind.SequenceEnd, _line, Column + 1, collectionStyle: YamlCollectionStyle.Flow));
    }

    /// <summary>Parses one flow-sequence entry, wrapping a trailing ':' into a single-pair mapping.</summary>
    private void ParseFlowEntry()
    {
        var startIndex = _events.Count;
        var (anchor, tag, _) = ParseInlineProperties();
        ParseFlowNode(anchor, tag);
        SkipFlowSeparation();

        if (Peek() == ':' && (IsSpaceLikeOrEnd(Peek(1)) || Peek(1) is ',' or ']' or '}' or '[' or '{' or '"' or '\''))
        {
            var keyEvent = _events[startIndex];
            _events.Insert(startIndex, new YamlEvent(YamlEventKind.MappingStart, keyEvent.Line, keyEvent.Column, collectionStyle: YamlCollectionStyle.Flow));
            Advance();
            SkipFlowSeparation();

            if (Peek() is ',' or ']' or '}')
            {
                EmitNullScalar(null, null);
            }
            else
            {
                var (valueAnchor, valueTag, _) = ParseInlineProperties();
                ParseFlowNode(valueAnchor, valueTag);
            }

            Emit(new YamlEvent(YamlEventKind.MappingEnd, _line, Column + 1, collectionStyle: YamlCollectionStyle.Flow));
        }
    }

    private void ParseFlowMapping(string? anchor, string? tag)
    {
        Emit(new YamlEvent(YamlEventKind.MappingStart, _line, Column + 1, anchor: anchor, tag: tag, collectionStyle: YamlCollectionStyle.Flow));
        Advance(); // '{'

        while (true)
        {
            SkipFlowSeparation();
            if (AtEnd)
            {
                throw Error("Unterminated flow mapping.");
            }

            if (Peek() == '}')
            {
                Advance();
                break;
            }

            if (Peek() == '?' && IsSpaceLikeOrEnd(Peek(1)))
            {
                Advance();
                SkipFlowSeparation();
            }

            // Key (a lone ':' means a null key).
            if (Peek() == ':' && (IsSpaceLikeOrEnd(Peek(1)) || Peek(1) is ',' or ']' or '}'))
            {
                EmitNullScalar(null, null);
            }
            else
            {
                var (keyAnchor, keyTag, _) = ParseInlineProperties();
                ParseFlowNode(keyAnchor, keyTag);
            }

            SkipFlowSeparation();

            if (Peek() == ':' && (IsSpaceLikeOrEnd(Peek(1)) || Peek(1) is ',' or ']' or '}' or '[' or '{' or '"' or '\''))
            {
                Advance();
                SkipFlowSeparation();

                if (Peek() is ',' or '}')
                {
                    EmitNullScalar(null, null);
                }
                else
                {
                    var (valueAnchor, valueTag, _) = ParseInlineProperties();
                    ParseFlowNode(valueAnchor, valueTag);
                }
            }
            else
            {
                EmitNullScalar(null, null);
            }

            SkipFlowSeparation();
            if (Peek() == ',')
            {
                Advance();
                continue;
            }

            if (Peek() == '}')
            {
                Advance();
                break;
            }

            throw Error("Expected ',' or '}' in a flow mapping.");
        }

        Emit(new YamlEvent(YamlEventKind.MappingEnd, _line, Column + 1, collectionStyle: YamlCollectionStyle.Flow));
    }

    private void ParseFlowScalarOrAlias(string? anchor, string? tag)
    {
        var c = Peek();
        var line = _line;
        var startColumn = Column;

        if (c == '*')
        {
            Advance();
            var name = ReadAnchorName();
            Emit(new YamlEvent(YamlEventKind.Alias, line, startColumn + 1, value: name));
            return;
        }

        if (c is '"' or '\'')
        {
            var singleLine = true;
            var value = c == '"'
                ? ScanDoubleQuoted(-1, ref singleLine)
                : ScanSingleQuoted(-1, ref singleLine);
            Emit(new YamlEvent(
                YamlEventKind.Scalar,
                line,
                startColumn + 1,
                value: value,
                anchor: anchor,
                tag: tag,
                scalarStyle: c == '"' ? YamlScalarStyle.DoubleQuoted : YamlScalarStyle.SingleQuoted));
            return;
        }

        var (text, _) = ScanPlainFirstLine(inFlow: true);
        if (text.Length == 0)
        {
            throw Error($"Unexpected character '{Peek()}' in flow context.");
        }

        // Multi-line plain scalars fold in flow context as well.
        var value2 = ContinuePlainMultilineFlow(text);
        Emit(new YamlEvent(YamlEventKind.Scalar, line, startColumn + 1, value: value2, anchor: anchor, tag: tag));
    }

    // ---------------------------------------------------------------- scalars

    /// <summary>Scans the current line's plain-scalar content, reporting whether it stopped at a key ':'.</summary>
    private (string Text, bool EndedAtColon) ScanPlainFirstLine(bool inFlow)
    {
        var builder = new StringBuilder();

        while (!AtEnd)
        {
            var c = Peek();
            if (c == '\n')
            {
                break;
            }

            if (c == '#' && builder.Length > 0 && (builder[^1] == ' ' || builder[^1] == '\t'))
            {
                break;
            }

            if (c == ':')
            {
                var next = Peek(1);
                if (IsSpaceLikeOrEnd(next) || (inFlow && next is ',' or ']' or '}'))
                {
                    return (TrimEnd(builder), true);
                }
            }

            if (inFlow && c is ',' or '[' or ']' or '{' or '}')
            {
                break;
            }

            builder.Append(c);
            Advance();
        }

        return (TrimEnd(builder), false);
    }

    /// <summary>Continues a block-context plain scalar across lines indented deeper than <paramref name="minIndent"/>.</summary>
    private string ContinuePlainMultiline(string firstLine, int minIndent)
    {
        var value = new StringBuilder(firstLine);
        var pendingBreaks = 0;

        while (true)
        {
            var state = Save();
            if (AtEnd)
            {
                break;
            }

            if (Peek() == '\n')
            {
                Advance();
            }

            // Measure the next line.
            SkipSpacesOnly();
            if (AtEnd)
            {
                Restore(state);
                break;
            }

            if (Peek() == '\n')
            {
                pendingBreaks++;
                continue;
            }

            if (AtDocumentStartMarker() || AtDocumentEndMarker() || Column <= minIndent || Peek() == '#')
            {
                Restore(state);
                break;
            }

            var (text, endedAtColon) = ScanPlainFirstLine(inFlow: false);
            if (endedAtColon || text.Length == 0)
            {
                Restore(state);
                break;
            }

            value.Append(pendingBreaks > 0 ? new string('\n', pendingBreaks) : " ");
            value.Append(text);
            pendingBreaks = 0;
        }

        return value.ToString();
    }

    private string ContinuePlainMultilineFlow(string firstLine)
    {
        var value = new StringBuilder(firstLine);
        var pendingBreaks = 0;

        while (true)
        {
            var state = Save();
            if (AtEnd || Peek() != '\n')
            {
                break;
            }

            Advance();
            SkipSpacesAndTabs();

            if (AtEnd || AtDocumentStartMarker() || AtDocumentEndMarker())
            {
                Restore(state);
                break;
            }

            if (Peek() == '\n')
            {
                pendingBreaks++;
                continue;
            }

            var (text, _) = ScanPlainFirstLine(inFlow: true);
            if (text.Length == 0)
            {
                Restore(state);
                break;
            }

            value.Append(pendingBreaks > 0 ? new string('\n', pendingBreaks) : " ");
            value.Append(text);
            pendingBreaks = 0;
        }

        return value.ToString();
    }

    private string ScanDoubleQuoted(int minIndent, ref bool singleLine)
    {
        Advance(); // opening '"'
        var builder = new StringBuilder();

        while (true)
        {
            if (AtEnd)
            {
                throw Error("Unterminated double-quoted scalar.");
            }

            var c = Peek();

            if (c == '"')
            {
                Advance();
                return builder.ToString();
            }

            if (c == '\\')
            {
                Advance();
                if (AtEnd)
                {
                    throw Error("Unterminated escape sequence.");
                }

                var escape = Peek();
                if (escape == '\n')
                {
                    // Escaped line break: continuation without a space.
                    singleLine = false;
                    Advance();
                    SkipSpacesAndTabs();
                    continue;
                }

                builder.Append(ReadEscape());
                continue;
            }

            if (c == '\n')
            {
                // Unescaped line breaks fold to a space; blank lines become breaks.
                singleLine = false;
                Advance();
                var breaks = 0;
                while (true)
                {
                    SkipSpacesAndTabs();
                    if (!AtEnd && Peek() == '\n')
                    {
                        Advance();
                        breaks++;
                        continue;
                    }

                    break;
                }

                TrimTrailingSpaces(builder);
                builder.Append(breaks > 0 ? new string('\n', breaks) : " ");
                continue;
            }

            builder.Append(c);
            Advance();
        }
    }

    private char ReadEscape()
    {
        var c = Peek();
        Advance();
        return c switch
        {
            '0' => '\0',
            'a' => '\a',
            'b' => '\b',
            't' or '\t' => '\t',
            'n' => '\n',
            'v' => '\v',
            'f' => '\f',
            'r' => '\r',
            'e' => '\x1b',
            ' ' => ' ',
            '"' => '"',
            '/' => '/',
            '\\' => '\\',
            'N' => '\x85',
            '_' => '\xa0',
            'L' => '\u2028',
            'P' => '\u2029',
            'x' => ReadHexEscape(2),
            'u' => ReadHexEscape(4),
            'U' => ReadHexEscape(8),
            _ => throw Error($"Unknown escape sequence '\\{c}'.")
        };
    }

    private char ReadHexEscape(int digits)
    {
        var value = 0;
        for (var index = 0; index < digits; index++)
        {
            if (AtEnd || !char.IsAsciiHexDigit(Peek()))
            {
                throw Error("Invalid hexadecimal escape sequence.");
            }

            value = (value << 4) + Convert.ToInt32(Peek().ToString(), 16);
            Advance();
        }

        // Characters outside the basic plane are not representable in a single char; the retained
        // scope surfaces them as a replacement pair through string.Append below when needed.
        if (value > char.MaxValue)
        {
            throw Error("Escape sequences beyond U+FFFF are not supported in this parser revision.");
        }

        return (char)value;
    }

    private string ScanSingleQuoted(int minIndent, ref bool singleLine)
    {
        Advance(); // opening '\''
        var builder = new StringBuilder();

        while (true)
        {
            if (AtEnd)
            {
                throw Error("Unterminated single-quoted scalar.");
            }

            var c = Peek();

            if (c == '\'')
            {
                if (Peek(1) == '\'')
                {
                    builder.Append('\'');
                    Advance(2);
                    continue;
                }

                Advance();
                return builder.ToString();
            }

            if (c == '\n')
            {
                singleLine = false;
                Advance();
                var breaks = 0;
                while (true)
                {
                    SkipSpacesAndTabs();
                    if (!AtEnd && Peek() == '\n')
                    {
                        Advance();
                        breaks++;
                        continue;
                    }

                    break;
                }

                TrimTrailingSpaces(builder);
                builder.Append(breaks > 0 ? new string('\n', breaks) : " ");
                continue;
            }

            builder.Append(c);
            Advance();
        }
    }

    private void ParseBlockScalar(int minIndent, bool folded, string? anchor, string? tag)
    {
        var line = _line;
        var startColumn = Column;
        Advance(); // '|' or '>'

        // Header indicators, in either order.
        var chomping = ' ';
        var explicitIndent = 0;
        for (var index = 0; index < 2; index++)
        {
            var c = AtEnd ? '\n' : Peek();
            if (c is '+' or '-' && chomping == ' ')
            {
                chomping = c;
                Advance();
            }
            else if (c is >= '1' and <= '9' && explicitIndent == 0)
            {
                explicitIndent = c - '0';
                Advance();
            }
        }

        SkipSpacesAndTabs();
        if (!AtEnd && Peek() == '#')
        {
            SkipRestOfLine();
        }

        if (!AtEnd && Peek() == '\n')
        {
            Advance();
        }
        else if (!AtEnd)
        {
            throw Error("Unexpected content after a block scalar header.");
        }

        var contentIndent = explicitIndent > 0 ? minIndent + 1 + (explicitIndent - 1) : -1;
        var lines = new List<string>();

        while (!AtEnd)
        {
            var state = Save();
            var indent = 0;
            while (!AtEnd && Peek() == ' ')
            {
                Advance();
                indent++;
            }

            if (AtEnd)
            {
                break;
            }

            if (Peek() == '\n')
            {
                Advance();
                lines.Add(string.Empty);
                continue;
            }

            if (contentIndent < 0)
            {
                if (indent <= minIndent)
                {
                    Restore(state);
                    break;
                }

                contentIndent = indent;
            }
            else if (indent < contentIndent)
            {
                Restore(state);
                break;
            }

            if (AtDocumentStartMarker() || AtDocumentEndMarker())
            {
                Restore(state);
                break;
            }

            var content = new string(' ', indent - contentIndent) + ReadWhile(static c => c != '\n');
            if (!AtEnd)
            {
                Advance();
            }

            lines.Add(content);
        }

        // Trailing empty lines participate in chomping only.
        var trailingEmpty = 0;
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
            trailingEmpty++;
        }

        string value;
        if (folded)
        {
            // Between adjacent non-empty lines a break folds to a space; N blank lines contribute
            // exactly N breaks; breaks around more-indented lines are kept literally.
            var builder = new StringBuilder();
            var previousWasIndented = false;
            var pendingEmpty = 0;
            var firstContent = true;
            foreach (var current in lines)
            {
                if (current.Length == 0)
                {
                    pendingEmpty++;
                    continue;
                }

                var isIndented = current[0] == ' ' || current[0] == '\t';

                if (!firstContent)
                {
                    if (pendingEmpty > 0)
                    {
                        builder.Append('\n', pendingEmpty);
                    }
                    else if (isIndented || previousWasIndented)
                    {
                        builder.Append('\n');
                    }
                    else
                    {
                        builder.Append(' ');
                    }
                }

                builder.Append(current);
                previousWasIndented = isIndented;
                firstContent = false;
                pendingEmpty = 0;
            }

            value = builder.ToString();
        }
        else
        {
            value = string.Join("\n", lines);
        }

        value = chomping switch
        {
            '-' => value,
            '+' => value.Length > 0 || trailingEmpty > 0
                ? value + new string('\n', trailingEmpty + (lines.Count > 0 ? 1 : 0))
                : value,
            _ => lines.Count > 0 ? value + "\n" : string.Empty
        };

        Emit(new YamlEvent(
            YamlEventKind.Scalar,
            line,
            startColumn + 1,
            value: value,
            anchor: anchor,
            tag: tag,
            scalarStyle: folded ? YamlScalarStyle.Folded : YamlScalarStyle.Literal));
    }

    // -------------------------------------------------------------- properties

    private (string? Anchor, string? Tag, bool HadProperties) ParseNodeProperties(int minIndent)
    {
        var (anchor, tag, had) = ParseInlineProperties();

        // Properties may sit on their own line above the node they describe.
        if (had && (AtEnd || Peek() == '\n' || Peek() == '#'))
        {
            var state = Save();
            SkipBlankAndCommentLines();
            if (AtEnd || AtDocumentStartMarker() || AtDocumentEndMarker() || Column <= minIndent)
            {
                Restore(state);
            }
        }

        return (anchor, tag, had);
    }

    private (string? Anchor, string? Tag, bool HadProperties) ParseInlineProperties()
    {
        string? anchor = null;
        string? tag = null;
        var had = false;

        while (!AtEnd)
        {
            var c = Peek();
            if (c == '&' && anchor is null)
            {
                Advance();
                anchor = ReadAnchorName();
                if (anchor.Length == 0)
                {
                    throw Error("An anchor requires a name.");
                }

                had = true;
                SkipSpacesAndTabs();
            }
            else if (c == '!' && tag is null)
            {
                tag = ReadTag();
                had = true;
                SkipSpacesAndTabs();
            }
            else
            {
                break;
            }
        }

        return (anchor, tag, had);
    }

    private string ReadAnchorName()
    {
        var builder = new StringBuilder();
        while (!AtEnd)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\n' or ',' or '[' or ']' or '{' or '}')
            {
                break;
            }

            if (c == ':' && IsSpaceLikeOrEnd(Peek(1)))
            {
                break;
            }

            builder.Append(c);
            Advance();
        }

        if (builder.Length == 0)
        {
            throw Error("An anchor or alias requires a name.");
        }

        return builder.ToString();
    }

    private string ReadTag()
    {
        Advance(); // '!'

        if (!AtEnd && Peek() == '<')
        {
            Advance();
            var uri = ReadWhile(static c => c is not '>' and not '\n');
            if (AtEnd || Peek() != '>')
            {
                throw Error("Unterminated verbatim tag.");
            }

            Advance();
            return uri;
        }

        var token = ReadWhile(static c => c is not ' ' and not '\t' and not '\n' and not ',' and not '[' and not ']' and not '{' and not '}');

        if (token.Length == 0)
        {
            return "!";
        }

        if (token[0] == '!')
        {
            // "!!suffix" — the secondary handle.
            return "tag:yaml.org,2002:" + token[1..];
        }

        var separator = token.IndexOf('!');
        if (separator > 0)
        {
            // "!handle!suffix" — a named handle declared by a %TAG directive.
            var handle = "!" + token[..(separator + 1)];
            if (_tagHandles is null || !_tagHandles.TryGetValue(handle, out var prefix))
            {
                throw Error($"Unknown tag handle '{handle}'.");
            }

            return prefix + token[(separator + 1)..];
        }

        if (_tagHandles is not null && _tagHandles.TryGetValue("!", out var primary))
        {
            return primary + token;
        }

        return "!" + token;
    }

    // ----------------------------------------------------------------- cursor

    private bool AtEnd => _pos >= _text.Length;

    private int Column => _pos - _lineStart;

    private char Peek(int offset = 0)
    {
        var index = _pos + offset;
        return index < _text.Length ? _text[index] : '\0';
    }

    private void Advance(int count = 1)
    {
        for (var index = 0; index < count && _pos < _text.Length; index++)
        {
            if (_text[_pos] == '\n')
            {
                _line++;
                _lineStart = _pos + 1;
            }

            _pos++;
        }
    }

    private (int Pos, int Line, int LineStart) Save() => (_pos, _line, _lineStart);

    private void Restore((int Pos, int Line, int LineStart) state)
    {
        _pos = state.Pos;
        _line = state.Line;
        _lineStart = state.LineStart;
    }

    private static bool IsSpaceLikeOrEnd(char c) => c is ' ' or '\t' or '\n' or '\0';

    private void SkipSpacesAndTabs()
    {
        while (!AtEnd && (Peek() == ' ' || Peek() == '\t'))
        {
            Advance();
        }
    }

    private void SkipSpacesOnly()
    {
        while (!AtEnd && Peek() == ' ')
        {
            Advance();
        }
    }

    private void SkipRestOfLine()
    {
        while (!AtEnd && Peek() != '\n')
        {
            Advance();
        }

        if (!AtEnd)
        {
            Advance();
        }
    }

    /// <summary>Skips blank lines, comment lines, and leading indentation, stopping at the next content character.</summary>
    private void SkipBlankAndCommentLines()
    {
        while (!AtEnd)
        {
            if (Peek() == ' ')
            {
                SkipSpacesOnly();
                continue;
            }

            if (Peek() == '\t')
            {
                // Tabs may separate content but must not indent it; the block parsers validate
                // columns, so a tab here is treated as separation whitespace.
                Advance();
                continue;
            }

            if (Peek() == '\n')
            {
                Advance();
                continue;
            }

            if (Peek() == '#')
            {
                SkipRestOfLine();
                continue;
            }

            break;
        }
    }

    /// <summary>Skips whitespace, line breaks, and comments between flow tokens.</summary>
    private void SkipFlowSeparation()
    {
        while (!AtEnd)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\n')
            {
                Advance();
                continue;
            }

            if (c == '#')
            {
                SkipRestOfLine();
                continue;
            }

            break;
        }
    }

    private void CheckNoTrailingContent()
    {
        SkipSpacesAndTabs();
        if (!AtEnd && Peek() != '\n' && Peek() != '#')
        {
            throw Error($"Unexpected content '{Peek()}' after a node.");
        }
    }

    private bool AtDocumentStartMarker() => AtLineMarker("---");

    private bool AtDocumentEndMarker() => AtLineMarker("...");

    private bool AtLineMarker(string marker)
    {
        if (Column != 0 || _pos + marker.Length > _text.Length)
        {
            return false;
        }

        for (var index = 0; index < marker.Length; index++)
        {
            if (_text[_pos + index] != marker[index])
            {
                return false;
            }
        }

        var after = Peek(marker.Length);
        return after is ' ' or '\t' or '\n' or '\0';
    }

    private string ReadWhile(Func<char, bool> predicate)
    {
        var start = _pos;
        while (!AtEnd && predicate(Peek()))
        {
            Advance();
        }

        return _text[start.._pos];
    }

    private static string TrimEnd(StringBuilder builder)
    {
        var length = builder.Length;
        while (length > 0 && (builder[length - 1] == ' ' || builder[length - 1] == '\t'))
        {
            length--;
        }

        return builder.ToString(0, length);
    }

    private static void TrimTrailingSpaces(StringBuilder builder)
    {
        while (builder.Length > 0 && (builder[^1] == ' ' || builder[^1] == '\t'))
        {
            builder.Length--;
        }
    }

    private void Emit(YamlEvent yamlEvent) => _events.Add(yamlEvent);

    private void EmitNullScalar(string? anchor, string? tag) =>
        Emit(new YamlEvent(YamlEventKind.Scalar, _line, Column + 1, value: null, anchor: anchor, tag: tag));

    private YamlException Error(string message) => new(message, _line, Column + 1);
}
