using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Renders a document as HTML in the shapes the CommonMark specification's examples use, so the
/// retained subset can be validated against spec-derived expectations. The walk is iterative (an
/// explicit op stack): parse-time depth caps bound containers but not inline nesting, and
/// caller-built documents have no caps at all.
/// </summary>
internal static class MarkdownHtmlRenderer
{
    public static string Render(MarkdownDocument document)
    {
        var builder = new StringBuilder();
        var ops = new Stack<Op>();
        PushChildren(ops, document.Blocks, tight: false, plain: false);
        while (ops.Count > 0)
        {
            var op = ops.Pop();
            if (op.Verbatim is not null)
            {
                if (op.NewLineBefore)
                {
                    AppendLineBreakIfNeeded(builder);
                }

                builder.Append(op.Verbatim);
                continue;
            }

            Render(builder, ops, op);
        }

        return builder.ToString();
    }

    private static void Render(StringBuilder builder, Stack<Op> ops, Op op)
    {
        switch (op.Node)
        {
            case MarkdownParagraph paragraph:
                if (op.Tight)
                {
                    PushChildren(ops, paragraph.Inlines, op.Tight, op.Plain);
                }
                else
                {
                    AppendLineBreakIfNeeded(builder);
                    builder.Append("<p>");
                    ops.Push(Op.ForVerbatim("</p>\n"));
                    PushChildren(ops, paragraph.Inlines, op.Tight, op.Plain);
                }

                break;

            case MarkdownHeading heading:
                AppendLineBreakIfNeeded(builder);
                builder.Append("<h").Append((char)('0' + heading.Level)).Append('>');
                ops.Push(Op.ForVerbatim($"</h{heading.Level}>\n"));
                PushChildren(ops, heading.Inlines, tight: false, op.Plain);
                break;

            case MarkdownBlockQuote quote:
                AppendLineBreakIfNeeded(builder);
                builder.Append("<blockquote>\n");
                ops.Push(Op.ForVerbatim("</blockquote>\n", newLineBefore: true));
                PushChildren(ops, quote.Blocks, tight: false, op.Plain);
                break;

            case MarkdownList list:
            {
                AppendLineBreakIfNeeded(builder);
                var tag = list.IsOrdered ? "ol" : "ul";
                builder.Append('<').Append(tag);
                if (list.IsOrdered && list.Start != 1)
                {
                    builder.Append(" start=\"").Append(list.Start).Append('"');
                }

                builder.Append(">\n");
                ops.Push(Op.ForVerbatim($"</{tag}>\n", newLineBefore: true));
                for (var index = list.Items.Count - 1; index >= 0; index--)
                {
                    ops.Push(Op.ForNode(list.Items[index], list.IsTight, op.Plain));
                }

                break;
            }

            case MarkdownListItem item:
                builder.Append("<li>");
                ops.Push(Op.ForVerbatim("</li>\n", newLineBefore: item.Blocks.Count > 0 && (!op.Tight || ContainsBlockChild(item))));
                PushChildren(ops, item.Blocks, op.Tight, op.Plain);
                break;

            case MarkdownCodeBlock code:
            {
                AppendLineBreakIfNeeded(builder);
                builder.Append("<pre><code");
                var language = FirstWord(code.Info);
                if (!language.IsEmpty)
                {
                    builder.Append(" class=\"language-");
                    AppendAttributeText(builder, language);
                    builder.Append('"');
                }

                builder.Append('>');
                AppendText(builder, code.Literal);
                builder.Append("</code></pre>\n");
                break;
            }

            case MarkdownThematicBreak:
                AppendLineBreakIfNeeded(builder);
                builder.Append("<hr />\n");
                break;

            case MarkdownLiteral literal:
                if (op.Plain)
                {
                    AppendAttributeText(builder, literal.Text);
                }
                else
                {
                    AppendText(builder, literal.Text);
                }

                break;

            case MarkdownEmphasis emphasis:
                if (!op.Plain)
                {
                    builder.Append("<em>");
                    ops.Push(Op.ForVerbatim("</em>"));
                }

                PushChildren(ops, emphasis.Inlines, op.Tight, op.Plain);
                break;

            case MarkdownStrong strong:
                if (!op.Plain)
                {
                    builder.Append("<strong>");
                    ops.Push(Op.ForVerbatim("</strong>"));
                }

                PushChildren(ops, strong.Inlines, op.Tight, op.Plain);
                break;

            case MarkdownCodeSpan span:
                if (!op.Plain)
                {
                    builder.Append("<code>");
                    AppendText(builder, span.Literal);
                    builder.Append("</code>");
                }
                else
                {
                    AppendAttributeText(builder, span.Literal);
                }

                break;

            case MarkdownLineBreak lineBreak:
                builder.Append(op.Plain ? " " : lineBreak.IsHard ? "<br />\n" : "\n");
                break;

            case MarkdownLink link:
                if (!op.Plain)
                {
                    builder.Append("<a href=\"");
                    AppendUrl(builder, link.Destination);
                    builder.Append('"');
                    if (link.Title is not null)
                    {
                        builder.Append(" title=\"");
                        AppendAttributeText(builder, link.Title);
                        builder.Append('"');
                    }

                    builder.Append('>');
                    ops.Push(Op.ForVerbatim("</a>"));
                }

                PushChildren(ops, link.Inlines, op.Tight, op.Plain);
                break;

            case MarkdownImage image:
                if (!op.Plain)
                {
                    builder.Append("<img src=\"");
                    AppendUrl(builder, image.Destination);
                    builder.Append("\" alt=\"");
                    var suffix = image.Title is not null
                        ? $"\" title=\"{EscapeAttribute(image.Title)}\" />"
                        : "\" />";
                    ops.Push(Op.ForVerbatim(suffix));
                    PushChildren(ops, image.Inlines, op.Tight, plain: true);
                }
                else
                {
                    PushChildren(ops, image.Inlines, op.Tight, op.Plain);
                }

                break;
        }
    }

    private static void PushChildren<T>(Stack<Op> ops, IList<T> children, bool tight, bool plain)
        where T : MarkdownNode
    {
        for (var index = children.Count - 1; index >= 0; index--)
        {
            ops.Push(Op.ForNode(children[index], tight, plain));
        }
    }

    private static bool ContainsBlockChild(MarkdownListItem item)
    {
        foreach (var block in item.Blocks)
        {
            if (block is not MarkdownParagraph)
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> FirstWord(string? info)
    {
        if (string.IsNullOrEmpty(info))
        {
            return [];
        }

        var span = info.AsSpan().Trim();
        var end = span.IndexOfAny(' ', '\t');
        return end < 0 ? span : span[..end];
    }

    private static void AppendLineBreakIfNeeded(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }

    private static void AppendText(StringBuilder builder, string text)
        => AppendAttributeText(builder, text);

    private static void AppendAttributeText(StringBuilder builder, ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            switch (character)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                default: builder.Append(character); break;
            }
        }
    }

    private static string EscapeAttribute(string text)
    {
        var builder = new StringBuilder(text.Length);
        AppendAttributeText(builder, text);
        return builder.ToString();
    }

    // The characters a destination may carry into an href unencoded — the reference
    // implementation's safe set. Everything else percent-encodes as UTF-8.
    private const string urlSafe = "!#$%&'()*+,-./0123456789:;=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]_abcdefghijklmnopqrstuvwxyz~";

    private static void AppendUrl(StringBuilder builder, string destination)
    {
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in destination.EnumerateRunes())
        {
            if (rune.IsAscii && urlSafe.Contains((char)rune.Value))
            {
                var character = (char)rune.Value;
                switch (character)
                {
                    case '&': builder.Append("&amp;"); break;
                    case '\'': builder.Append("&#39;"); break;
                    default: builder.Append(character); break;
                }
            }
            else
            {
                var length = rune.EncodeToUtf8(utf8);
                for (var index = 0; index < length; index++)
                {
                    builder.Append('%').Append(utf8[index].ToString("X2"));
                }
            }
        }
    }

    private sealed class Op
    {
        public MarkdownNode? Node { get; private init; }

        public string? Verbatim { get; private init; }

        public bool NewLineBefore { get; private init; }

        public bool Tight { get; private init; }

        public bool Plain { get; private init; }

        public static Op ForNode(MarkdownNode node, bool tight, bool plain)
            => new() { Node = node, Tight = tight, Plain = plain };

        public static Op ForVerbatim(string text, bool newLineBefore = false)
            => new() { Verbatim = text, NewLineBefore = newLineBefore };
    }
}
