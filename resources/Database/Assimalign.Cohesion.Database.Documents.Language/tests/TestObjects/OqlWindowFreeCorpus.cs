using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests.TestObjects;

internal static class OqlWindowFreeCorpus
{
    internal static readonly string[] Words = ["OVER", "WINDOW", "PARTITION", "RESPECT", "IGNORE", "ROWS", "RANGE", "GROUPS", "EXCLUDE",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "RATIO_TO_REPORT"];

    // Rename each distinct spelling throughout its statement, preserving uses of its binding.
    // The base acceptance bitmap decides which identifier positions belong to the language.
    internal static IEnumerable<string> Candidates(string text)
    {
        yield return text;
        var lexer = new TokenLexer(text, OqlLanguageProfile.Instance.ToLexerOptions());
        var tokens = new List<(TokenType Type, string Text, int Start)>();
        while (lexer.MoveNext())
        {
            var token = lexer.Current;
            if (token.Type != TokenType.Comment) { tokens.Add((token.Type, token.Value.ToString(), token.Position)); }
        }
        int statement = tokens.Count > 0 && tokens[0].Text.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        bool indexDdl = tokens.Count > statement + 2 && tokens[statement].Text.Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
            (tokens[statement + 1].Text.Equals("INDEX", StringComparison.OrdinalIgnoreCase) ||
             tokens[statement + 1].Text.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) && tokens[statement + 2].Text.Equals("INDEX", StringComparison.OrdinalIgnoreCase));
        // Classify and group replacement spans once. Each rename then visits only
        // its own spans, preserving the recorded ordinal spelling/word order.
        var positions = Enumerable.Range(0, tokens.Count).Where(IsName)
            .GroupBy(index => tokens[index].Text, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.ToArray()).ToArray();
        IEnumerable<int[]> groups = text.Length > 100_000 ?
            [positions.SelectMany(group => group).Order().ToArray()] : positions;
        foreach (var group in groups)
        {
            foreach (string word in Words)
            {
                var builder = new StringBuilder(text.Length);
                int end = 0;
                foreach (int index in group)
                {
                    var token = tokens[index];
                    builder.Append(text.AsSpan(end, token.Start - end));
                    builder.Append(word);
                    end = token.Start + token.Text.Length;
                }
                builder.Append(text.AsSpan(end));
                yield return builder.ToString();
            }
        }

        bool IsName(int index)
        {
            var token = tokens[index];
            if (token.Type is not (TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function)) { return false; }
            if (index + 1 == tokens.Count || tokens[index + 1].Type != TokenType.LeftParen) { return true; }
            // These header names precede lists, rather than invoke functions.
            return index > 0 && (tokens[index - 1].Text.Equals("INTO", StringComparison.OrdinalIgnoreCase) ||
                tokens[index - 1].Text.Equals("AS", StringComparison.OrdinalIgnoreCase) ||
                tokens[index - 1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                indexDdl);
        }
    }

    internal static bool HasReservedCall(string text)
    {
        // Most renames place the vocabulary in names, not calls. Avoid lexing those
        // candidates a second time; this is only a conservative negative fast path.
        bool possible = false;
        for (int word = 9; word < Words.Length && !possible; word++)
        {
            int index = 0;
            while ((index = text.IndexOf(Words[word], index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                index += Words[word].Length;
                int next = index;
                if (next < text.Length && text[next] == '"') { next++; }
                while (next < text.Length && char.IsWhiteSpace(text[next])) { next++; }
                if (next < text.Length && (text[next] == '(' || text[next] is '/' or '-' or '#')) { possible = true; break; }
            }
        }
        if (!possible) { return false; }
        var lexer = new TokenLexer(text, OqlLanguageProfile.Instance.ToLexerOptions());
        var tokens = new List<(TokenType Type, string Text)>();
        while (lexer.MoveNext())
        {
            var token = lexer.Current;
            if (token.Type != TokenType.Comment) { tokens.Add((token.Type, token.Value.ToString())); }
        }
        int start = tokens.Count > 0 && tokens[0].Text.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        bool indexDdl = tokens.Count > start + 2 && tokens[start].Text.Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
            (tokens[start + 1].Text.Equals("INDEX", StringComparison.OrdinalIgnoreCase) ||
             tokens[start + 1].Text.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) && tokens[start + 2].Text.Equals("INDEX", StringComparison.OrdinalIgnoreCase));
        for (int index = 0; index + 1 < tokens.Count; index++)
        {
            var token = tokens[index];
            if (tokens[index + 1].Type != TokenType.LeftParen || token.Type is not
                (TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function)) { continue; }
            if (index > 0 && (tokens[index - 1].Text.Equals("INTO", StringComparison.OrdinalIgnoreCase) ||
                tokens[index - 1].Text.Equals("AS", StringComparison.OrdinalIgnoreCase) ||
                indexDdl && tokens[index - 1].Text.Equals("ON", StringComparison.OrdinalIgnoreCase))) { continue; }
            string name = token.Type == TokenType.QuotedIdentifier ? token.Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : token.Text;
            if (Words.Skip(9).Contains(name, StringComparer.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    internal static string? Canonical(OqlExpression expression)
    {
        try { return Render(expression); }
        // Wrapping a depth-limit SELECT adds a level that has no standalone renderer.
        catch (InvalidOperationException) { return null; }
    }

    private static string? Render(OqlExpression expression) => expression switch
    {
        OqlMergeExpression merge => merge.ToString(),
        OqlQueryExpression query => new OqlMergeExpression("target", "t", new OqlMergeSource("", "s", new OqlSubqueryExpression(query)),
            new OqlLiteralExpression("key"), true, delete: new OqlMergeDeleteAction()).ToString(),
        OqlExplainExpression explain => Canonical(explain.Statement) is string text ? "EXPLAIN " + text : null,
        OqlTransactionExpression transaction => transaction.ToString(),
        _ => null,
    };
}
