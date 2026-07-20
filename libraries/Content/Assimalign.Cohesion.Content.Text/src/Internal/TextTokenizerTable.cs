using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// The compiled form of <see cref="TextTokenizerOptions"/>: definitions grouped by first character
/// and ordered longest-first within each group, plus the candidate-character set the tokenizer
/// scans for. Compilation snapshots the options — later mutations do not affect this table.
/// </summary>
internal sealed class TextTokenizerTable
{
    private readonly Dictionary<char, TextTokenDefinition[]> _groups;

    private TextTokenizerTable(
        Dictionary<char, TextTokenDefinition[]> groups,
        char[] candidates,
        bool tokenizeWhitespace,
        bool whitespaceOverlapsDefinitions)
    {
        _groups = groups;
        Candidates = candidates;
        TokenizeWhitespace = tokenizeWhitespace;
        WhitespaceOverlapsDefinitions = whitespaceOverlapsDefinitions;
    }

    /// <summary>The table for default options: the three new-line definitions, no whitespace runs.</summary>
    public static TextTokenizerTable Default { get; } = Create(new TextTokenizerOptions());

    /// <summary>Every character that can start a token: definition first characters, plus space and tab when whitespace runs are tokenized.</summary>
    public char[] Candidates { get; }

    /// <summary>Whether runs of space and tab characters are emitted as whitespace tokens.</summary>
    public bool TokenizeWhitespace { get; }

    /// <summary>Whether any definition starts with a space or tab, requiring per-character definition checks inside whitespace runs.</summary>
    public bool WhitespaceOverlapsDefinitions { get; }

    /// <summary>
    /// Compiles options into a match table, validating the definition list.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the definition list contains a null entry or two definitions with the same text.</exception>
    public static TextTokenizerTable Create(TextTokenizerOptions options)
    {
        var byFirstChar = new Dictionary<char, List<TextTokenDefinition>>();
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in options.Tokens)
        {
            if (definition is null)
            {
                throw new ArgumentException("Token definitions must not be null.", nameof(options));
            }

            if (!texts.Add(definition.Text))
            {
                throw new ArgumentException($"Duplicate token definition text '{Escape(definition.Text)}'.", nameof(options));
            }

            var first = definition.Text[0];
            if (!byFirstChar.TryGetValue(first, out var group))
            {
                byFirstChar[first] = group = [];
            }

            group.Add(definition);
        }

        var groups = new Dictionary<char, TextTokenDefinition[]>(byFirstChar.Count);
        foreach (var (first, group) in byFirstChar)
        {
            // Longest first, so the longest literal sharing a first character wins ("**" over "*").
            group.Sort(static (a, b) => b.Text.Length.CompareTo(a.Text.Length));
            groups[first] = [.. group];
        }

        var candidateCount = groups.Count;
        var tokenizeWhitespace = options.TokenizeWhitespace;
        if (tokenizeWhitespace)
        {
            candidateCount += (groups.ContainsKey(' ') ? 0 : 1) + (groups.ContainsKey('\t') ? 0 : 1);
        }

        var candidates = new char[candidateCount];
        var index = 0;
        foreach (var first in groups.Keys)
        {
            candidates[index++] = first;
        }

        if (tokenizeWhitespace)
        {
            if (!groups.ContainsKey(' '))
            {
                candidates[index++] = ' ';
            }

            if (!groups.ContainsKey('\t'))
            {
                candidates[index] = '\t';
            }
        }

        var whitespaceOverlaps = tokenizeWhitespace && (groups.ContainsKey(' ') || groups.ContainsKey('\t'));
        return new TextTokenizerTable(groups, candidates, tokenizeWhitespace, whitespaceOverlaps);
    }

    /// <summary>
    /// Tries to match a definition at the reader's current position, testing the longest literal
    /// first among definitions sharing the current character.
    /// </summary>
    /// <param name="reader">The reader positioned at the candidate character.</param>
    /// <param name="advancePast"><see langword="true"/> to consume the matched literal; <see langword="false"/> to test without consuming.</param>
    /// <param name="definition">The matched definition, when one matched.</param>
    /// <returns><see langword="true"/> when a definition matched.</returns>
    public bool TryMatch(ref SequenceReader<char> reader, bool advancePast, [NotNullWhen(true)] out TextTokenDefinition? definition)
    {
        if (reader.TryPeek(out var first) && _groups.TryGetValue(first, out var group))
        {
            foreach (var candidate in group)
            {
                if (reader.IsNext(candidate.Text.AsSpan(), advancePast))
                {
                    definition = candidate;
                    return true;
                }
            }
        }

        definition = null;
        return false;
    }

    private static string Escape(string text)
        => text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
