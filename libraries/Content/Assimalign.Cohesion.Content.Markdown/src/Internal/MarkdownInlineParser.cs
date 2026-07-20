using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// The inline phase of the parser: tokenizes a leaf block's raw content with a
/// <see cref="TextTokenizer"/> delimiter table and applies the CommonMark inline strategy — code
/// spans and autolinks bind first in a left-to-right pass, brackets resolve to inline links and
/// images, and emphasis resolves through the delimiter-stack algorithm with the flanking rules.
/// Constructs outside the retained subset (raw HTML, reference links, unknown named entities)
/// degrade to literal text.
/// </summary>
internal static class MarkdownInlineParser
{
    private const int backtickId = 1;
    private const int starId = 2;
    private const int underscoreId = 3;
    private const int openBracketId = 4;
    private const int closeBracketId = 5;
    private const int bangId = 6;
    private const int lessId = 7;
    private const int backslashId = 8;
    private const int ampersandId = 9;

    private static readonly TextTokenizerOptions TokenizerOptions = CreateTokenizerOptions();

    private static TextTokenizerOptions CreateTokenizerOptions()
    {
        var options = new TextTokenizerOptions();
        options.Tokens.Add(new TextTokenDefinition("`", backtickId));
        options.Tokens.Add(new TextTokenDefinition("*", starId));
        options.Tokens.Add(new TextTokenDefinition("_", underscoreId));
        options.Tokens.Add(new TextTokenDefinition("[", openBracketId));
        options.Tokens.Add(new TextTokenDefinition("]", closeBracketId));
        options.Tokens.Add(new TextTokenDefinition("!", bangId));
        options.Tokens.Add(new TextTokenDefinition("<", lessId));
        options.Tokens.Add(new TextTokenDefinition("\\", backslashId));
        options.Tokens.Add(new TextTokenDefinition("&", ampersandId));
        return options;
    }

    /// <summary>Parses raw inline content into <paramref name="target"/>.</summary>
    public static void Parse(string raw, IList<MarkdownInline> target)
    {
        raw = raw.Replace('\0', '�').TrimEnd();
        if (raw.Length == 0)
        {
            return;
        }

        var tokens = new List<TextToken>();
        var tokenizer = new TextTokenizer(raw, TokenizerOptions);
        while (tokenizer.TryRead(out var token))
        {
            tokens.Add(token);
        }

        var atoms = new List<Atom>();
        var literal = new StringBuilder();
        long consumed = 0;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var offset = (int)token.Position.Offset;
            var end = offset + (int)token.Value.Length;
            if (end <= consumed)
            {
                continue;
            }

            if (token.Kind == TextTokenKind.Text)
            {
                literal.Append(raw.AsSpan(Math.Max(offset, (int)consumed), end - Math.Max(offset, (int)consumed)));
                consumed = end;
                continue;
            }

            if (token.Kind == TextTokenKind.NewLine)
            {
                var hard = TrimTrailingSpaces(literal) >= 2;
                Flush(atoms, literal);
                atoms.Add(Atom.ForNode(new MarkdownLineBreak(hard)));
                consumed = end;
                continue;
            }

            switch (token.Id)
            {
                case backtickId:
                {
                    var run = CountRun(tokens, index, backtickId);
                    if (TryReadCodeSpan(raw, tokens, index, run, out var span, out var spanEnd))
                    {
                        Flush(atoms, literal);
                        atoms.Add(Atom.ForNode(span));
                        consumed = spanEnd;
                    }
                    else
                    {
                        literal.Append('`', run);
                        consumed = offset + run;
                    }

                    break;
                }

                case backslashId:
                {
                    if (offset + 1 < raw.Length && raw[offset + 1] == '\n')
                    {
                        Flush(atoms, literal);
                        atoms.Add(Atom.ForNode(new MarkdownLineBreak(isHard: true)));
                        consumed = offset + 2;
                    }
                    else if (offset + 1 < raw.Length && IsAsciiPunctuation(raw[offset + 1]))
                    {
                        literal.Append(raw[offset + 1]);
                        consumed = offset + 2;
                    }
                    else
                    {
                        literal.Append('\\');
                        consumed = end;
                    }

                    break;
                }

                case ampersandId:
                {
                    if (TryResolveEntity(raw.AsSpan(offset), out var resolved, out var length))
                    {
                        literal.Append(resolved);
                        consumed = offset + length;
                    }
                    else
                    {
                        literal.Append('&');
                        consumed = end;
                    }

                    break;
                }

                case lessId:
                {
                    if (TryReadAutolink(raw, offset, out var link, out var linkEnd))
                    {
                        Flush(atoms, literal);
                        atoms.Add(Atom.ForNode(link));
                        consumed = linkEnd;
                    }
                    else
                    {
                        literal.Append('<');
                        consumed = end;
                    }

                    break;
                }

                case starId:
                case underscoreId:
                {
                    var run = CountRun(tokens, index, token.Id);
                    var character = token.Id == starId ? '*' : '_';
                    var before = offset > 0 ? raw[offset - 1] : ' ';
                    var after = offset + run < raw.Length ? raw[offset + run] : ' ';
                    var (canOpen, canClose) = Classify(character, before, after);
                    Flush(atoms, literal);
                    atoms.Add(Atom.ForDelimiter(new Delimiter(character, run) { CanOpen = canOpen, CanClose = canClose }));
                    consumed = offset + run;
                    break;
                }

                case bangId:
                {
                    if (offset + 1 < raw.Length && raw[offset + 1] == '[')
                    {
                        Flush(atoms, literal);
                        atoms.Add(Atom.ForDelimiter(new Delimiter('[', 1) { IsImage = true }));
                        consumed = offset + 2;
                    }
                    else
                    {
                        literal.Append('!');
                        consumed = end;
                    }

                    break;
                }

                case openBracketId:
                {
                    Flush(atoms, literal);
                    atoms.Add(Atom.ForDelimiter(new Delimiter('[', 1)));
                    consumed = end;
                    break;
                }

                case closeBracketId:
                {
                    Flush(atoms, literal);
                    consumed = ProcessCloseBracket(raw, atoms, end, literal);
                    break;
                }

                default:
                {
                    literal.Append(raw.AsSpan(offset, end - offset));
                    consumed = end;
                    break;
                }
            }
        }

        Flush(atoms, literal);
        ProcessEmphasis(atoms, 0);
        Materialize(atoms, 0, target);
    }

    /// <summary>Resolves backslash escapes only (used for fence info strings).</summary>
    public static string ResolveEscapes(string text)
    {
        if (!text.Contains('\\'))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length && IsAsciiPunctuation(text[index + 1]))
            {
                index++;
            }

            builder.Append(text[index]);
        }

        return builder.ToString();
    }

    /// <summary>Resolves backslash escapes and character references (destinations and titles).</summary>
    private static string ResolveEscapesAndEntities(ReadOnlySpan<char> text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\' && index + 1 < text.Length && IsAsciiPunctuation(text[index + 1]))
            {
                builder.Append(text[index + 1]);
                index++;
            }
            else if (character == '&' && TryResolveEntity(text[index..], out var resolved, out var length))
            {
                builder.Append(resolved);
                index += length - 1;
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void Flush(List<Atom> atoms, StringBuilder literal)
    {
        if (literal.Length > 0)
        {
            atoms.Add(Atom.ForNode(new MarkdownLiteral(literal.ToString())));
            literal.Clear();
        }
    }

    private static int TrimTrailingSpaces(StringBuilder literal)
    {
        var count = 0;
        while (literal.Length > 0 && literal[^1] is ' ' or '\t')
        {
            if (literal[^1] == ' ')
            {
                count++;
            }

            literal.Length--;
        }

        return count;
    }

    private static int CountRun(List<TextToken> tokens, int index, int id)
    {
        var run = 1;
        var nextOffset = tokens[index].Position.Offset + 1;
        for (var next = index + 1; next < tokens.Count; next++)
        {
            if (tokens[next].Id != id || tokens[next].Position.Offset != nextOffset)
            {
                break;
            }

            run++;
            nextOffset++;
        }

        return run;
    }

    private static bool TryReadCodeSpan(string raw, List<TextToken> tokens, int index, int run, out MarkdownCodeSpan span, out int spanEnd)
    {
        span = null!;
        spanEnd = 0;
        var openEnd = (int)tokens[index].Position.Offset + run;
        for (var next = index + 1; next < tokens.Count; next++)
        {
            if (tokens[next].Id != backtickId || tokens[next].Position.Offset < openEnd)
            {
                continue;
            }

            var closeRun = CountRun(tokens, next, backtickId);
            if (closeRun != run)
            {
                next += closeRun - 1;
                continue;
            }

            var start = openEnd;
            var closeStart = (int)tokens[next].Position.Offset;
            var content = raw[start..closeStart].Replace('\n', ' ');
            if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.AsSpan().Trim().Length > 0)
            {
                content = content[1..^1];
            }

            span = new MarkdownCodeSpan(content);
            spanEnd = closeStart + closeRun;
            return true;
        }

        return false;
    }

    private static bool TryReadAutolink(string raw, int offset, out MarkdownLink link, out int end)
    {
        link = null!;
        end = 0;
        var index = offset + 1;
        if (index >= raw.Length || !char.IsAsciiLetter(raw[index]))
        {
            return false;
        }

        var schemeLength = 1;
        index++;
        while (index < raw.Length && schemeLength < 32
            && (char.IsAsciiLetterOrDigit(raw[index]) || raw[index] is '+' or '.' or '-'))
        {
            schemeLength++;
            index++;
        }

        if (schemeLength < 2 || index >= raw.Length || raw[index] != ':')
        {
            return false;
        }

        index++;
        while (index < raw.Length)
        {
            var character = raw[index];
            if (character == '>')
            {
                var uri = raw[(offset + 1)..index];
                link = new MarkdownLink(uri);
                link.Inlines.Add(new MarkdownLiteral(uri));
                end = index + 1;
                return true;
            }

            if (character is ' ' or '\t' or '\n' or '<' || char.IsControl(character))
            {
                return false;
            }

            index++;
        }

        return false;
    }

    /// <summary>
    /// Handles a <c>]</c>: finds the most recent bracket, tries the inline <c>(destination "title")</c>
    /// suffix, and on success wraps the atoms since the bracket into a link or image (processing
    /// their emphasis first, as the spec requires). Anything else degrades to literal brackets —
    /// including reference-style links, which are outside the retained subset.
    /// </summary>
    private static long ProcessCloseBracket(string raw, List<Atom> atoms, int bracketEnd, StringBuilder literal)
    {
        var bracketIndex = -1;
        for (var index = atoms.Count - 1; index >= 0; index--)
        {
            if (atoms[index].Delimiter is { Character: '[' })
            {
                bracketIndex = index;
                break;
            }
        }

        if (bracketIndex < 0)
        {
            literal.Append(']');
            return bracketEnd;
        }

        var bracket = atoms[bracketIndex].Delimiter!;
        if (!bracket.Active
            || !TryReadLinkSuffix(raw, bracketEnd, out var destination, out var title, out var suffixEnd))
        {
            atoms[bracketIndex] = Atom.ForNode(new MarkdownLiteral(bracket.IsImage ? "![" : "["));
            literal.Append(']');
            return bracketEnd;
        }

        ProcessEmphasis(atoms, bracketIndex + 1);
        MarkdownInline node;
        IList<MarkdownInline> children;
        if (bracket.IsImage)
        {
            var image = new MarkdownImage(destination) { Title = title };
            children = image.Inlines;
            node = image;
        }
        else
        {
            var link = new MarkdownLink(destination) { Title = title };
            children = link.Inlines;
            node = link;

            // Links may not contain links: earlier link openers can no longer match.
            for (var index = 0; index < bracketIndex; index++)
            {
                if (atoms[index].Delimiter is { Character: '[', IsImage: false } earlier)
                {
                    earlier.Active = false;
                }
            }
        }

        Materialize(atoms, bracketIndex + 1, children);
        atoms.RemoveRange(bracketIndex, atoms.Count - bracketIndex);
        atoms.Add(Atom.ForNode(node));
        return suffixEnd;
    }

    private static bool TryReadLinkSuffix(string raw, int start, out string destination, out string? title, out int end)
    {
        destination = string.Empty;
        title = null;
        end = 0;
        var index = start;
        if (index >= raw.Length || raw[index] != '(')
        {
            return false;
        }

        index = SkipLinkWhitespace(raw, index + 1);
        if (index >= raw.Length)
        {
            return false;
        }

        int destinationStart;
        int destinationEnd;
        if (raw[index] == '<')
        {
            destinationStart = index + 1;
            index++;
            while (index < raw.Length && raw[index] != '>')
            {
                if (raw[index] is '\n' or '<')
                {
                    return false;
                }

                // A backslash escape may carry < or > inside the angle form.
                if (raw[index] == '\\' && index + 1 < raw.Length)
                {
                    index++;
                }

                index++;
            }

            if (index >= raw.Length)
            {
                return false;
            }

            destinationEnd = index;
            index++;
        }
        else
        {
            destinationStart = index;
            var depth = 0;
            while (index < raw.Length)
            {
                var character = raw[index];
                if (character == '\\' && index + 1 < raw.Length && IsAsciiPunctuation(raw[index + 1]))
                {
                    index += 2;
                    continue;
                }

                if (character is ' ' or '\t' or '\n' || char.IsControl(character))
                {
                    break;
                }

                if (character == '(')
                {
                    if (++depth > 32)
                    {
                        return false;
                    }
                }
                else if (character == ')')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }

                index++;
            }

            if (depth != 0)
            {
                return false;
            }

            destinationEnd = index;
        }

        var afterDestination = index;
        index = SkipLinkWhitespace(raw, index);
        if (index < raw.Length && raw[index] is '"' or '\'' or '(' && index > afterDestination)
        {
            var opener = raw[index];
            var closer = opener == '(' ? ')' : opener;
            var titleStart = index + 1;
            index++;
            while (index < raw.Length && raw[index] != closer)
            {
                if (opener == '(' && raw[index] == '(')
                {
                    return false;
                }

                if (raw[index] == '\\' && index + 1 < raw.Length)
                {
                    index++;
                }

                index++;
            }

            if (index >= raw.Length)
            {
                return false;
            }

            title = ResolveEscapesAndEntities(raw.AsSpan(titleStart, index - titleStart));
            index = SkipLinkWhitespace(raw, index + 1);
        }

        if (index >= raw.Length || raw[index] != ')')
        {
            return false;
        }

        destination = ResolveEscapesAndEntities(raw.AsSpan(destinationStart, destinationEnd - destinationStart));
        end = index + 1;
        return true;
    }

    private static int SkipLinkWhitespace(string raw, int index)
    {
        while (index < raw.Length && raw[index] is ' ' or '\t' or '\n')
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// The spec's delimiter-stack emphasis algorithm over the atom list, from
    /// <paramref name="stackBottom"/> on: closers search back for the nearest compatible opener
    /// (honoring the multiple-of-three rule), strong pairs bind before single pairs, and the wrapped
    /// range becomes an emphasis or strong node in place.
    /// </summary>
    private static void ProcessEmphasis(List<Atom> atoms, int stackBottom)
    {
        var current = stackBottom;
        while (current < atoms.Count)
        {
            var closer = atoms[current].Delimiter;
            if (closer is null || closer.Character == '[' || !closer.CanClose || closer.Count == 0)
            {
                current++;
                continue;
            }

            var openerIndex = -1;
            for (var index = current - 1; index >= stackBottom; index--)
            {
                var candidate = atoms[index].Delimiter;
                if (candidate is null || candidate.Character != closer.Character || !candidate.CanOpen || candidate.Count == 0)
                {
                    continue;
                }

                // The multiple-of-three rule prevents mismatched runs like ***a*b** from pairing
                // across incompatible boundaries.
                if ((closer.CanOpen || candidate.CanClose)
                    && (candidate.OriginalCount + closer.OriginalCount) % 3 == 0
                    && (candidate.OriginalCount % 3 != 0 || closer.OriginalCount % 3 != 0))
                {
                    continue;
                }

                openerIndex = index;
                break;
            }

            if (openerIndex < 0)
            {
                if (!closer.CanOpen)
                {
                    atoms[current] = Atom.ForNode(new MarkdownLiteral(new string(closer.Character, closer.Count)));
                }

                current++;
                continue;
            }

            var opener = atoms[openerIndex].Delimiter!;
            var use = opener.Count >= 2 && closer.Count >= 2 ? 2 : 1;
            MarkdownInline wrapper;
            IList<MarkdownInline> children;
            if (use == 2)
            {
                var strong = new MarkdownStrong();
                children = strong.Inlines;
                wrapper = strong;
            }
            else
            {
                var emphasis = new MarkdownEmphasis();
                children = emphasis.Inlines;
                wrapper = emphasis;
            }

            Materialize(atoms, openerIndex + 1, current, children);
            atoms.RemoveRange(openerIndex + 1, current - openerIndex - 1);
            atoms.Insert(openerIndex + 1, Atom.ForNode(wrapper));

            opener.Count -= use;
            closer.Count -= use;
            current = openerIndex + 2;
            if (opener.Count == 0)
            {
                atoms.RemoveAt(openerIndex);
                current--;
            }

            if (closer.Count == 0)
            {
                atoms.RemoveAt(current);
            }
        }
    }

    /// <summary>Converts a range of atoms into inline nodes, degrading leftover delimiters to literal text and merging adjacent literals.</summary>
    private static void Materialize(List<Atom> atoms, int start, IList<MarkdownInline> target)
        => Materialize(atoms, start, atoms.Count, target);

    private static void Materialize(List<Atom> atoms, int start, int end, IList<MarkdownInline> target)
    {
        MarkdownLiteral? pending = null;
        for (var index = start; index < end; index++)
        {
            var atom = atoms[index];
            MarkdownInline node;
            if (atom.Delimiter is { } delimiter)
            {
                if (delimiter.Count == 0)
                {
                    continue;
                }

                var text = delimiter.Character == '[' ? (delimiter.IsImage ? "![" : "[") : new string(delimiter.Character, delimiter.Count);
                node = new MarkdownLiteral(text);
            }
            else
            {
                node = atom.Node!;
            }

            if (node is MarkdownLiteral incoming && pending is not null)
            {
                pending.Text += incoming.Text;
                continue;
            }

            pending = node as MarkdownLiteral;
            target.Add(node);
        }
    }

    private static (bool CanOpen, bool CanClose) Classify(char character, char before, char after)
    {
        var beforeWhitespace = char.IsWhiteSpace(before);
        var afterWhitespace = char.IsWhiteSpace(after);
        var beforePunctuation = IsUnicodePunctuation(before);
        var afterPunctuation = IsUnicodePunctuation(after);

        var leftFlanking = !afterWhitespace && (!afterPunctuation || beforeWhitespace || beforePunctuation);
        var rightFlanking = !beforeWhitespace && (!beforePunctuation || afterWhitespace || afterPunctuation);

        if (character == '*')
        {
            return (leftFlanking, rightFlanking);
        }

        return (
            leftFlanking && (!rightFlanking || beforePunctuation),
            rightFlanking && (!leftFlanking || afterPunctuation));
    }

    private static bool IsUnicodePunctuation(char character)
        => char.IsPunctuation(character) || char.IsSymbol(character);

    private static bool IsAsciiPunctuation(char character)
        => character is > ' ' and < (char)127 && !char.IsAsciiLetterOrDigit(character);

    /// <summary>
    /// Resolves a character reference at the start of the span: numeric (decimal or hexadecimal)
    /// references, or the five XML-predefined named entities of the retained subset. Unknown names
    /// stay literal.
    /// </summary>
    private static bool TryResolveEntity(ReadOnlySpan<char> text, out string resolved, out int length)
    {
        resolved = string.Empty;
        length = 0;
        if (text.Length < 3 || text[0] != '&')
        {
            return false;
        }

        if (text[1] == '#')
        {
            var hex = text.Length > 2 && text[2] is 'x' or 'X';
            var index = hex ? 3 : 2;
            var digits = 0;
            var value = 0;
            while (index < text.Length && digits < (hex ? 6 : 7))
            {
                var character = text[index];
                int digit;
                if (char.IsAsciiDigit(character))
                {
                    digit = character - '0';
                }
                else if (hex && char.IsAsciiHexDigit(character))
                {
                    digit = char.ToLowerInvariant(character) - 'a' + 10;
                }
                else
                {
                    break;
                }

                value = (value * (hex ? 16 : 10)) + digit;
                digits++;
                index++;
            }

            if (digits == 0 || index >= text.Length || text[index] != ';')
            {
                return false;
            }

            if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF)
            {
                resolved = "�";
            }
            else
            {
                resolved = char.ConvertFromUtf32(value);
            }

            length = index + 1;
            return true;
        }

        foreach (var (name, replacement) in NamedEntities)
        {
            if (text.Length > name.Length + 1
                && text[1..(name.Length + 1)].SequenceEqual(name)
                && text[name.Length + 1] == ';')
            {
                resolved = replacement;
                length = name.Length + 2;
                return true;
            }
        }

        return false;
    }

    private static readonly (string Name, string Replacement)[] NamedEntities =
    [
        ("amp", "&"),
        ("lt", "<"),
        ("gt", ">"),
        ("quot", "\""),
        ("apos", "'"),
    ];

    private sealed class Atom
    {
        public MarkdownInline? Node { get; private init; }

        public Delimiter? Delimiter { get; private init; }

        public static Atom ForNode(MarkdownInline node) => new() { Node = node };

        public static Atom ForDelimiter(Delimiter delimiter) => new() { Delimiter = delimiter };
    }

    private sealed class Delimiter(char character, int count)
    {
        public char Character { get; } = character;

        public int Count { get; set; } = count;

        public int OriginalCount { get; } = count;

        public bool CanOpen { get; init; }

        public bool CanClose { get; init; }

        public bool IsImage { get; init; }

        public bool Active { get; set; } = true;
    }
}
