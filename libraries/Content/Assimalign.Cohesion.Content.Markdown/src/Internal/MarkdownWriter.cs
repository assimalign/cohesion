using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Writes a document back to canonical Markdown. The emitted form is normalized — ATX headings,
/// <c>-</c> bullets, backtick fences, <c>---</c> breaks, alternating <c>*</c>/<c>_</c> emphasis by
/// nesting depth, and conservative literal escaping — chosen so that re-parsing a written document
/// yields an equivalent tree (the round-trip guarantee for parser-produced documents). The walk is
/// iterative for the same stack-safety reasons as the HTML renderer.
/// </summary>
internal sealed class MarkdownWriter
{
    private readonly StringBuilder _builder = new();
    private readonly List<string> _prefixes = [];
    private readonly Stack<Op> _ops = new();
    private bool _atLineStart = true;

    public static string Write(MarkdownDocument document)
    {
        var writer = new MarkdownWriter();
        writer.PushBlocks(document.Blocks, tight: false);
        writer.Run();
        return writer._builder.ToString();
    }

    private void Run()
    {
        while (_ops.Count > 0)
        {
            var op = _ops.Pop();
            switch (op.Kind)
            {
                case OpKind.Raw:
                    WriteContent(op.Text!);
                    break;

                case OpKind.NewLine:
                    _builder.Append('\n');
                    _atLineStart = true;
                    break;

                case OpKind.BlankLine:
                    EnsureLineStart();
                    foreach (var prefix in _prefixes)
                    {
                        _builder.Append(prefix.AsSpan().TrimEnd());
                    }

                    _builder.Append('\n');
                    break;

                case OpKind.PushPrefix:
                    _prefixes.Add(op.Text!);
                    if (op.EmitIfMidLine && !_atLineStart)
                    {
                        // A container opening mid-line (a quote as the first block of a list item)
                        // still owns the rest of the current line; the prefix machinery covers only
                        // the lines that follow.
                        _builder.Append(op.Text);
                    }

                    break;

                case OpKind.PopPrefix:
                    _prefixes.RemoveAt(_prefixes.Count - 1);
                    break;

                case OpKind.Literal:
                    WriteLiteral(op.Text!, op.NextStartsBracket);
                    break;

                case OpKind.Inlines:
                    PushInlines(op.InlineList!, op.EmphasisDepth);
                    break;

                case OpKind.Node:
                    Write(op);
                    break;
            }
        }

        EnsureLineStart();
    }

    private void Write(Op op)
    {
        switch (op.Node)
        {
            case MarkdownParagraph paragraph:
                _ops.Push(Op.NewLineOp);
                PushInlines(paragraph.Inlines, emphasisDepth: 0);
                break;

            case MarkdownHeading heading:
                _ops.Push(Op.NewLineOp);
                PushInlines(heading.Inlines, emphasisDepth: 0);
                _ops.Push(Op.Raw(new string('#', heading.Level) + " "));
                break;

            case MarkdownThematicBreak:
                _ops.Push(Op.NewLineOp);
                _ops.Push(Op.Raw("---"));
                break;

            case MarkdownCodeBlock code:
                PushCodeBlock(code);
                break;

            case MarkdownBlockQuote quote:
                _ops.Push(Op.PopPrefixOp);
                if (quote.Blocks.Count == 0)
                {
                    _ops.Push(Op.NewLineOp);
                    _ops.Push(Op.Raw(string.Empty));
                }
                else
                {
                    PushBlocks(quote.Blocks, tight: false);
                }

                _ops.Push(Op.PushPrefix("> ", emitIfMidLine: true));
                break;

            case MarkdownList list:
                PushList(list);
                break;
        }
    }

    /// <summary>Pushes sibling blocks; loose contexts separate them with blank lines.</summary>
    private void PushBlocks(IList<MarkdownBlock> blocks, bool tight)
    {
        for (var index = blocks.Count - 1; index >= 0; index--)
        {
            _ops.Push(Op.ForNode(blocks[index]));
            if (index > 0 && !tight)
            {
                _ops.Push(Op.BlankLineOp);
            }
        }
    }

    private void PushList(MarkdownList list)
    {
        for (var index = list.Items.Count - 1; index >= 0; index--)
        {
            var item = list.Items[index];
            var marker = list.IsOrdered
                ? (list.Start + index).ToString() + ". "
                : "- ";

            _ops.Push(Op.PopPrefixOp);
            if (item.Blocks.Count == 0)
            {
                _ops.Push(Op.NewLineOp);
            }
            else
            {
                PushBlocks(item.Blocks, list.IsTight);
            }

            _ops.Push(Op.PushPrefix(new string(' ', marker.Length)));
            _ops.Push(Op.Raw(marker));
            if (index > 0 && !list.IsTight)
            {
                _ops.Push(Op.BlankLineOp);
            }
        }
    }

    private void PushCodeBlock(MarkdownCodeBlock code)
    {
        // A backtick fence longer than any run in the content; a tilde fence when the info string
        // itself carries a backtick.
        var fenceChar = code.Info?.Contains('`') is true ? '~' : '`';
        var fence = new string(fenceChar, Math.Max(3, LongestRun(code.Literal, fenceChar) + 1));

        _ops.Push(Op.NewLineOp);
        _ops.Push(Op.Raw(fence));
        if (code.Literal.Length > 0)
        {
            _ops.Push(Op.Raw(code.Literal.EndsWith('\n') ? code.Literal : code.Literal + "\n"));
        }

        _ops.Push(Op.NewLineOp);
        _ops.Push(Op.Raw(EscapeFenceInfo(code.Info) is { Length: > 0 } info ? fence + info : fence));
    }

    private void PushInlines(IList<MarkdownInline> inlines, int emphasisDepth)
    {
        for (var index = inlines.Count - 1; index >= 0; index--)
        {
            var inline = inlines[index];
            switch (inline)
            {
                case MarkdownLiteral literal:
                    _ops.Push(Op.ForLiteral(literal.Text, NextStartsBracket(inlines, index)));
                    break;

                case MarkdownEmphasis emphasis:
                {
                    var delimiter = emphasisDepth % 2 == 0 ? "*" : "_";
                    _ops.Push(Op.Raw(delimiter));
                    _ops.Push(Op.ForInlines(emphasis.Inlines, emphasisDepth + 1));
                    _ops.Push(Op.Raw(delimiter));
                    break;
                }

                case MarkdownStrong strong:
                {
                    var delimiter = emphasisDepth % 2 == 0 ? "**" : "__";
                    _ops.Push(Op.Raw(delimiter));
                    _ops.Push(Op.ForInlines(strong.Inlines, emphasisDepth + 1));
                    _ops.Push(Op.Raw(delimiter));
                    break;
                }

                case MarkdownCodeSpan span:
                    _ops.Push(Op.Raw(FormatCodeSpan(span.Literal)));
                    break;

                case MarkdownLineBreak lineBreak:
                    _ops.Push(Op.NewLineOp);
                    if (lineBreak.IsHard)
                    {
                        _ops.Push(Op.Raw("\\"));
                    }

                    break;

                case MarkdownLink link:
                    _ops.Push(Op.Raw(FormatDestinationSuffix(link.Destination, link.Title)));
                    _ops.Push(Op.ForInlines(link.Inlines, emphasisDepth));
                    _ops.Push(Op.Raw("["));
                    break;

                case MarkdownImage image:
                    _ops.Push(Op.Raw(FormatDestinationSuffix(image.Destination, image.Title)));
                    _ops.Push(Op.ForInlines(image.Inlines, emphasisDepth));
                    _ops.Push(Op.Raw("!["));
                    break;
            }
        }
    }

    private static bool NextStartsBracket(IList<MarkdownInline> inlines, int index)
        => index + 1 < inlines.Count
            && (inlines[index + 1] is MarkdownLink or MarkdownImage
                || (inlines[index + 1] is MarkdownLiteral { Text.Length: > 0 } literal && literal.Text[0] == '['));

    /// <summary>Writes raw text through the prefix machinery (prefixes appear after every newline).</summary>
    private void WriteContent(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (_atLineStart)
            {
                foreach (var prefix in _prefixes)
                {
                    _builder.Append(prefix);
                }

                _atLineStart = false;
            }

            _builder.Append(character);
            if (character == '\n')
            {
                _atLineStart = true;
            }
        }

        // An empty raw op still forces the prefix out (used to anchor an empty block quote).
        if (text.IsEmpty && _atLineStart)
        {
            foreach (var prefix in _prefixes)
            {
                _builder.Append(prefix);
            }

            _atLineStart = false;
        }
    }

    /// <summary>
    /// Writes literal text with the conservative escape set that guarantees re-parse fidelity:
    /// every structural inline character escapes, plus the line-start block markers and
    /// <c>!</c> when a bracket follows.
    /// </summary>
    private void WriteLiteral(string text, bool nextStartsBracket)
    {
        var startedAtLineStart = _atLineStart;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var escape = character switch
            {
                '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '&' => true,
                '#' or '-' or '+' or '>' or '~' => startedAtLineStart && index == 0,
                '!' => index + 1 < text.Length ? text[index + 1] == '[' : nextStartsBracket,
                '.' or ')' => startedAtLineStart && IsOrderedMarkerPosition(text, index),
                _ => false,
            };

            if (escape)
            {
                WriteContent("\\");
            }

            WriteContent(text.AsSpan(index, 1));
        }
    }

    /// <summary>An ordered-list delimiter needs escaping when the line so far is only its digits.</summary>
    private static bool IsOrderedMarkerPosition(string text, int index)
    {
        if (index is 0 or > 9)
        {
            return false;
        }

        for (var position = 0; position < index; position++)
        {
            if (!char.IsAsciiDigit(text[position]))
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureLineStart()
    {
        if (!_atLineStart)
        {
            _builder.Append('\n');
            _atLineStart = true;
        }
    }

    private static int LongestRun(string text, char character)
    {
        var longest = 0;
        var current = 0;
        foreach (var candidate in text)
        {
            current = candidate == character ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static string FormatCodeSpan(string literal)
    {
        var fence = new string('`', Math.Max(1, LongestRun(literal, '`') + 1));
        var pad = literal.Length == 0
            || literal[0] is '`' or ' '
            || literal[^1] is '`' or ' ';
        return pad ? $"{fence} {literal} {fence}" : fence + literal + fence;
    }

    private static string FormatDestinationSuffix(string destination, string? title)
    {
        var builder = new StringBuilder("](");
        var needsAngle = destination.Length == 0 && title is not null;
        foreach (var character in destination)
        {
            if (character is ' ' or '\t' or '\n' or '(' or ')' or '<' or '>' || char.IsControl(character))
            {
                needsAngle = true;
                break;
            }
        }

        if (needsAngle)
        {
            builder.Append('<');
            foreach (var character in destination)
            {
                if (character is '<' or '>' or '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(character == '\n' ? ' ' : character);
            }

            builder.Append('>');
        }
        else
        {
            builder.Append(destination);
        }

        if (title is not null)
        {
            builder.Append(" \"");
            foreach (var character in title)
            {
                if (character is '"' or '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(character);
            }

            builder.Append('"');
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string? EscapeFenceInfo(string? info)
    {
        if (string.IsNullOrEmpty(info))
        {
            return info;
        }

        var builder = new StringBuilder(info.Length);
        foreach (var character in info)
        {
            if (character == '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character == '\n' ? ' ' : character);
        }

        return builder.ToString();
    }

    private enum OpKind
    {
        Node,
        Raw,
        Literal,
        Inlines,
        NewLine,
        BlankLine,
        PushPrefix,
        PopPrefix,
    }

    private sealed class Op
    {
        public OpKind Kind { get; private init; }

        public MarkdownNode? Node { get; private init; }

        public string? Text { get; private init; }

        public IList<MarkdownInline>? InlineList { get; private init; }

        public int EmphasisDepth { get; private init; }

        public bool NextStartsBracket { get; private init; }

        public bool EmitIfMidLine { get; private init; }

        public static Op NewLineOp { get; } = new() { Kind = OpKind.NewLine };

        public static Op BlankLineOp { get; } = new() { Kind = OpKind.BlankLine };

        public static Op PopPrefixOp { get; } = new() { Kind = OpKind.PopPrefix };

        public static Op ForNode(MarkdownNode node) => new() { Kind = OpKind.Node, Node = node };

        public static Op Raw(string text) => new() { Kind = OpKind.Raw, Text = text };

        public static Op ForLiteral(string text, bool nextStartsBracket)
            => new() { Kind = OpKind.Literal, Text = text, NextStartsBracket = nextStartsBracket };

        public static Op ForInlines(IList<MarkdownInline> inlines, int emphasisDepth)
            => new() { Kind = OpKind.Inlines, InlineList = inlines, EmphasisDepth = emphasisDepth };

        public static Op PushPrefix(string prefix, bool emitIfMidLine = false)
            => new() { Kind = OpKind.PushPrefix, Text = prefix, EmitIfMidLine = emitIfMidLine };
    }
}
