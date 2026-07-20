using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// The block phase of the parser: consumes the input line by line (via <see cref="TextTokenizer"/>
/// with default options), maintains the stack of open containers per the CommonMark block strategy,
/// and collects raw inline content for <see cref="MarkdownInlineParser"/> to process once the tree
/// is complete. Constructs excluded from the retained subset (setext headings, indented code, HTML
/// blocks, link reference definitions) fall through to paragraph text — parsing never throws for
/// input.
/// </summary>
internal sealed class MarkdownBlockParser
{
    // Containers beyond this depth degrade to paragraph text instead of nesting further, which also
    // bounds renderer stack depth.
    private const int maxContainerDepth = 128;

    private readonly MarkdownDocument _document = new();
    private readonly List<Container> _open = [];
    private readonly List<(IList<MarkdownInline> Target, string Raw)> _pendingInlines = [];

    // At most one leaf is open at a time, always attached under the deepest open container.
    private MarkdownParagraph? _paragraph;
    private StringBuilder? _paragraphRaw;
    private MarkdownCodeBlock? _fence;
    private StringBuilder? _fenceRaw;
    private char _fenceChar;
    private int _fenceLength;
    private int _fenceIndent;

    private MarkdownBlockParser()
    {
        _open.Add(new Container(ContainerKind.Document, _document.Blocks));
    }

    public static MarkdownDocument Parse(string text)
    {
        var parser = new MarkdownBlockParser();
        var tokenizer = new TextTokenizer(text);
        ReadOnlySpan<char> line = default;
        var hasText = false;
        while (tokenizer.TryRead(out var token))
        {
            if (token.Kind == TextTokenKind.Text)
            {
                line = token.Value.IsSingleSegment ? token.Value.FirstSpan : token.Value.ToString().AsSpan();
                hasText = true;
            }
            else
            {
                parser.ProcessLine(hasText ? line : []);
                line = default;
                hasText = false;
            }
        }

        if (hasText)
        {
            parser.ProcessLine(line);
        }

        parser.CloseTo(0);
        foreach (var (target, raw) in parser._pendingInlines)
        {
            MarkdownInlineParser.Parse(raw, target);
        }

        return parser._document;
    }

    private void ProcessLine(ReadOnlySpan<char> line)
    {
        var cursor = new LineCursor(line);

        // 1. Match the continuation prefix of each open container, outermost first.
        var matched = MatchContinuations(ref cursor, out var lostEmptyItem);
        var allMatched = matched == _open.Count && !lostEmptyItem;

        // 2. An open fence consumes the line whenever its containers matched; otherwise it closes
        //    with them (fences never continue lazily).
        if (_fence is not null && allMatched)
        {
            HandleFenceLine(ref cursor);
            return;
        }

        // 3. Blank line: closes any leaf and every container that did not mark itself on this line
        //    (a blank always separates block quotes; list items match blanks explicitly and stay
        //    open), then feeds looseness tracking.
        if (cursor.IsBlank)
        {
            CloseTo(matched - 1);
            HandleBlank(matched);
            return;
        }

        // 4. Try block starts. Container starts consume their marker and loop for more.
        var openedAny = false;
        while (true)
        {
            if (_open.Count >= maxContainerDepth)
            {
                break;
            }

            var probe = cursor;
            var indent = probe.SkipWhitespace(maxColumns: 4);
            if (indent >= 4)
            {
                // Spec: indented code. Retained subset has none — the line is paragraph text.
                break;
            }

            if (!probe.AtEnd && probe.Current == '>')
            {
                probe.Advance();
                probe.SkipOneSpace();
                CloseTo(matched - 1);
                OpenContainer(new Container(ContainerKind.BlockQuote, AttachBlockQuote()));
                matched = _open.Count;
                cursor = probe;
                openedAny = true;
                continue;
            }

            if (TryReadThematicBreak(probe))
            {
                CloseTo(matched - 1);
                AttachBlock(new MarkdownThematicBreak());
                return;
            }

            if (TryReadAtxHeading(probe, out var level, out var headingRaw))
            {
                CloseTo(matched - 1);
                var heading = new MarkdownHeading(level);
                AttachBlock(heading);
                _pendingInlines.Add((heading.Inlines, headingRaw));
                return;
            }

            if (TryReadFenceOpen(ref probe, indent, out var fenceChar, out var fenceLength, out var info))
            {
                CloseTo(matched - 1);
                var fence = new MarkdownCodeBlock { Info = info };
                AttachBlock(fence);
                _fence = fence;
                _fenceRaw = new StringBuilder();
                _fenceChar = fenceChar;
                _fenceLength = fenceLength;
                _fenceIndent = indent;
                return;
            }

            if (TryReadListItemStart(ref cursor, ref matched, openedAny))
            {
                openedAny = true;
                continue;
            }

            break;
        }

        // 5. Remaining text: paragraph content. A paragraph still open from the previous line
        //    absorbs the text even when deeper containers stopped matching (lazy continuation).
        cursor.SkipWhitespace();
        var text = cursor.Remaining;
        if (_paragraph is not null && !openedAny)
        {
            _paragraphRaw!.Append('\n').Append(text);
            return;
        }

        CloseTo(matched - 1);
        var paragraph = new MarkdownParagraph();
        AttachBlock(paragraph);
        _paragraph = paragraph;
        _paragraphRaw = new StringBuilder();
        _paragraphRaw.Append(text);
    }

    /// <summary>
    /// Matches container prefixes outermost-in, consuming markers from the cursor. Returns the
    /// count of matched containers (the document pseudo-container always matches).
    /// <paramref name="lostEmptyItem"/> reports a content-less item hitting its second blank line —
    /// a list item may begin with at most one blank line.
    /// </summary>
    private int MatchContinuations(ref LineCursor cursor, out bool lostEmptyItem)
    {
        lostEmptyItem = false;
        var matched = 1;
        while (matched < _open.Count)
        {
            var container = _open[matched];
            switch (container.Kind)
            {
                case ContainerKind.BlockQuote:
                {
                    var probe = cursor;
                    probe.SkipWhitespace(maxColumns: 3);
                    if (probe.AtEnd || probe.Current != '>')
                    {
                        return matched;
                    }

                    probe.Advance();
                    probe.SkipOneSpace();
                    cursor = probe;
                    break;
                }

                case ContainerKind.List:
                    // The list matches through its current item (or a sibling marker, handled at
                    // block starts).
                    break;

                case ContainerKind.ListItem:
                {
                    if (cursor.IsBlank)
                    {
                        if (!container.HasContent && container.BlankPending)
                        {
                            lostEmptyItem = true;
                            return matched;
                        }

                        break;
                    }

                    var probe = cursor;
                    probe.SkipWhitespace(maxColumns: container.ContentColumn - probe.Column);
                    if (probe.Column < container.ContentColumn)
                    {
                        return matched;
                    }

                    cursor = probe;
                    break;
                }
            }

            matched++;
        }

        return matched;
    }

    private void HandleBlank(int matched)
    {
        for (var index = matched - 1; index > 0; index--)
        {
            var container = _open[index];
            if (container.Kind == ContainerKind.ListItem)
            {
                // A blank before any content is the single allowed leading blank; one after content
                // arms looseness.
                container.BlankPending = true;
            }
        }
    }

    private void HandleFenceLine(ref LineCursor cursor)
    {
        var probe = cursor;
        var indent = probe.SkipWhitespace(maxColumns: 4);
        if (indent <= 3 && !probe.AtEnd && probe.Current == _fenceChar)
        {
            var run = probe.CountRun(_fenceChar);
            if (run >= _fenceLength)
            {
                var rest = probe.Remaining[run..];
                if (rest.IsWhiteSpace() || rest.IsEmpty)
                {
                    CloseLeaf();
                    return;
                }
            }
        }

        // Content line: remove up to the opening fence's indentation.
        cursor.SkipWhitespace(maxColumns: _fenceIndent);
        _fenceRaw!.Append(cursor.Remaining).Append('\n');
    }

    private static bool TryReadThematicBreak(LineCursor probe)
    {
        if (probe.AtEnd || probe.Current is not ('-' or '_' or '*'))
        {
            return false;
        }

        var marker = probe.Current;
        var count = 0;
        foreach (var character in probe.Remaining)
        {
            if (character == marker)
            {
                count++;
            }
            else if (character is not (' ' or '\t'))
            {
                return false;
            }
        }

        return count >= 3;
    }

    private static bool TryReadAtxHeading(LineCursor probe, out int level, out string raw)
    {
        level = 0;
        raw = string.Empty;
        var text = probe.Remaining;
        while (level < text.Length && text[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6 || (level < text.Length && text[level] is not (' ' or '\t')))
        {
            return false;
        }

        var content = text[level..].Trim();

        // Strip an optional closing sequence: a run of #'s that is all that remains or is preceded
        // by a space.
        var end = content.Length;
        while (end > 0 && content[end - 1] == '#')
        {
            end--;
        }

        if (end != content.Length && (end == 0 || content[end - 1] is ' ' or '\t'))
        {
            content = content[..end].TrimEnd();
        }

        raw = content.ToString();
        return true;
    }

    private static bool TryReadFenceOpen(ref LineCursor probe, int indent, out char fenceChar, out int fenceLength, out string? info)
    {
        fenceChar = default;
        fenceLength = 0;
        info = null;
        if (probe.AtEnd || probe.Current is not ('`' or '~'))
        {
            return false;
        }

        fenceChar = probe.Current;
        fenceLength = probe.CountRun(fenceChar);
        if (fenceLength < 3)
        {
            return false;
        }

        var rest = probe.Remaining[fenceLength..].Trim();
        if (fenceChar == '`' && rest.Contains('`'))
        {
            // An info string on a backtick fence cannot contain backticks (it would be ambiguous
            // with an inline code span).
            return false;
        }

        info = rest.IsEmpty ? null : MarkdownInlineParser.ResolveEscapes(rest.ToString());
        return true;
    }

    /// <summary>
    /// Tries to start a list item (and, when needed, its list) at the cursor. Handles sibling items
    /// of the current list, marker-type changes starting a new list, and the paragraph-interruption
    /// restrictions (only a nonempty item, and for ordered lists only a start of one, may interrupt
    /// a paragraph).
    /// </summary>
    private bool TryReadListItemStart(ref LineCursor cursor, ref int matched, bool openedContainer)
    {
        var probe = cursor;
        probe.SkipWhitespace(maxColumns: 3);
        if (probe.AtEnd)
        {
            return false;
        }

        var isOrdered = false;
        var start = 0;
        char marker;
        if (probe.Current is '-' or '+' or '*')
        {
            marker = probe.Current;
            probe.Advance();
        }
        else if (char.IsAsciiDigit(probe.Current))
        {
            // At most nine digits, per the spec — and comfortably inside int.
            var digits = 0;
            while (!probe.AtEnd && char.IsAsciiDigit(probe.Current) && digits < 9)
            {
                start = (start * 10) + (probe.Current - '0');
                probe.Advance();
                digits++;
            }

            if (probe.AtEnd || probe.Current is not ('.' or ')'))
            {
                return false;
            }

            isOrdered = true;
            marker = probe.Current;
            probe.Advance();
        }
        else
        {
            return false;
        }

        // The marker must be followed by whitespace or end the line.
        if (!probe.AtEnd && probe.Current is not (' ' or '\t'))
        {
            return false;
        }

        var markerEndColumn = probe.Column;
        var spacesAfter = probe.SkipWhitespace(maxColumns: 5);
        int contentColumn;
        var startsBlank = probe.AtEnd;
        if (startsBlank || spacesAfter > 4)
        {
            // An item starting blank, or with five-plus spaces after its marker, requires content
            // indented exactly one column past the marker.
            contentColumn = markerEndColumn + 1;
            probe = cursor;
            probe.SkipWhitespace(maxColumns: 3);
            probe.SkipMarker(markerEndColumn);
            probe.SkipWhitespace(maxColumns: 1);
        }
        else
        {
            contentColumn = markerEndColumn + spacesAfter;
        }

        // Paragraph interruption restrictions apply when the marker line would otherwise continue
        // an open paragraph in the fully matched chain.
        if (_paragraph is not null && !openedContainer && matched == _open.Count
            && (startsBlank || (isOrdered && start != 1)))
        {
            return false;
        }

        // A marker while the current item is still matched starts a nested list inside it; a marker
        // after the item stopped matching is a sibling of (or replacement for) the current list.
        CloseTo(matched - 1);
        var deepest = _open[^1];
        Container listContainer;
        if (deepest.Kind == ContainerKind.List && deepest.ListMarker == marker)
        {
            listContainer = deepest;
            if (listContainer.BlankPending)
            {
                listContainer.List!.IsTight = false;
                listContainer.BlankPending = false;
            }
        }
        else
        {
            if (deepest.Kind == ContainerKind.List)
            {
                // Same level, different marker kind: the current list ends and a new one starts.
                CloseTo(_open.Count - 2);
            }

            var list = new MarkdownList(isOrdered) { IsTight = true };
            if (isOrdered)
            {
                list.Start = start;
            }

            AttachBlock(list);
            listContainer = new Container(ContainerKind.List, items: null)
            {
                List = list,
                ListMarker = marker,
            };
            OpenContainer(listContainer);
        }

        var item = new MarkdownListItem();
        listContainer.List!.Add(item);
        var itemContainer = new Container(ContainerKind.ListItem, item.Blocks)
        {
            List = listContainer.List,
            ContentColumn = contentColumn,
            BlankPending = startsBlank,
        };
        OpenContainer(itemContainer);
        matched = _open.Count;
        cursor = probe;
        return true;
    }

    private void OpenContainer(Container container)
    {
        CloseLeaf();
        _open.Add(container);
    }

    private IList<MarkdownBlock> AttachBlockQuote()
    {
        var quote = new MarkdownBlockQuote();
        AttachBlock(quote);
        return quote.Blocks;
    }

    /// <summary>
    /// Attaches a block under the deepest open container, closing any open leaf first and feeding
    /// list looseness when the attachment follows a blank line inside an item.
    /// </summary>
    private void AttachBlock(MarkdownBlock block)
    {
        CloseLeaf();
        var container = _open[^1];
        if (container.Kind == ContainerKind.List)
        {
            // Only items may live in a list; anything else closes it.
            CloseTo(_open.Count - 2);
            container = _open[^1];
        }

        if (container.Kind == ContainerKind.ListItem)
        {
            if (container.BlankPending && container.HasContent)
            {
                container.List!.IsTight = false;
            }

            container.BlankPending = false;
            container.HasContent = true;
        }

        container.Blocks!.Add(block);
    }

    private void CloseLeaf()
    {
        if (_paragraph is not null)
        {
            _pendingInlines.Add((_paragraph.Inlines, _paragraphRaw!.ToString()));
            _paragraph = null;
            _paragraphRaw = null;
        }

        if (_fence is not null)
        {
            _fence.Literal = _fenceRaw!.ToString();
            _fence = null;
            _fenceRaw = null;
        }
    }

    /// <summary>Closes open containers so that <paramref name="depth"/> is the deepest remaining index.</summary>
    private void CloseTo(int depth)
    {
        CloseLeaf();
        for (var index = _open.Count - 1; index > depth; index--)
        {
            var container = _open[index];
            if (container.Kind == ContainerKind.ListItem && container.BlankPending && container.HasContent)
            {
                // A blank at the end of an item loosens the list only if another item follows;
                // arm the list and let the next item start decide.
                _open[index - 1].BlankPending = true;
            }

            _open.RemoveAt(index);
        }
    }

    private enum ContainerKind
    {
        Document,
        BlockQuote,
        List,
        ListItem,
    }

    private sealed class Container(ContainerKind kind, IList<MarkdownBlock>? items)
    {
        public ContainerKind Kind { get; } = kind;
        public IList<MarkdownBlock>? Blocks { get; } = items;
        public MarkdownList? List { get; init; }
        public char ListMarker { get; init; }
        public int ContentColumn { get; init; }
        public bool BlankPending { get; set; }
        public bool HasContent { get; set; }
    }

    /// <summary>
    /// A tab-aware cursor over one line: columns advance to the next multiple-of-four stop on tabs.
    /// A tab that straddles a requested column budget is consumed whole — the subset's documented
    /// simplification of the spec's partial-tab expansion.
    /// </summary>
    private ref struct LineCursor(ReadOnlySpan<char> line)
    {
        private readonly ReadOnlySpan<char> _line = line;

        public int Index { get; private set; }

        public int Column { get; private set; }

        public readonly bool AtEnd => Index >= _line.Length;

        public readonly char Current => _line[Index];

        public readonly ReadOnlySpan<char> Remaining => _line[Index..];

        public readonly bool IsBlank => Remaining.IsEmpty || Remaining.IsWhiteSpace();

        public void Advance()
        {
            Column += _line[Index] == '\t' ? 4 - (Column % 4) : 1;
            Index++;
        }

        /// <summary>Consumes spaces and tabs, up to a column budget; returns the columns consumed.</summary>
        public int SkipWhitespace(int maxColumns = int.MaxValue)
        {
            var startColumn = Column;
            while (!AtEnd && Column - startColumn < maxColumns && Current is ' ' or '\t')
            {
                Advance();
            }

            return Column - startColumn;
        }

        /// <summary>Consumes a single optional space (or a whole tab) after a container marker.</summary>
        public void SkipOneSpace()
        {
            if (!AtEnd && Current is ' ' or '\t')
            {
                Advance();
            }
        }

        /// <summary>Consumes marker characters until the given column is reached (for re-scanning).</summary>
        public void SkipMarker(int endColumn)
        {
            while (!AtEnd && Column < endColumn)
            {
                Advance();
            }
        }

        /// <summary>Counts the length of the run of <paramref name="character"/> at the cursor.</summary>
        public readonly int CountRun(char character)
        {
            var count = 0;
            var remaining = Remaining;
            while (count < remaining.Length && remaining[count] == character)
            {
                count++;
            }

            return count;
        }
    }
}
