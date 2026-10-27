using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>A statement cut out of the editor text, with its offset in that text.</summary>
internal readonly record struct ScriptStatement(string Text, int Offset);

/// <summary>
/// Splits editor text into statements at every <c>;</c> the engines' shared <see cref="TokenLexer"/>
/// reads as a semicolon token. Every engine parses one statement per call (SQL, OQL and GQL all
/// reject multiple statements), so the Studio runs a script statement by statement. The
/// terminating <c>;</c> stays with its statement.
/// </summary>
/// <remarks>
/// The splitter lexes rather than scanning for quotes and comments itself, so a statement ends
/// exactly where the engines read one: a <c>--</c> comment ends at any line terminator (#1150),
/// block comments nest, and in GQL <c>&lt;--</c> is an arrow followed by a minus sign. SQL and
/// OQL keep <c>&lt;</c> followed by a line comment. A character
/// scanner used to end a block comment at its first <c>*/</c>, so
/// <c>/* a /* b */ ; DELETE FROM t; */</c> ran a DELETE every engine reads as a comment. There is
/// no <c>//</c> comment in any language, so a <c>//</c> line reaches the engine, which rejects it,
/// instead of being skipped as blank.
/// </remarks>
internal static class ScriptSplitter
{
    public static List<ScriptStatement> Split(string text, StudioModel model)
    {
        var statements = new List<ScriptStatement>();
        int start = 0;

        TokenLexer lexer = Lex(text, model);
        while (lexer.MoveNext())
        {
            Token token = lexer.Current;
            if (token.Type == TokenType.Semicolon)
            {
                int end = token.Position + token.Value.Length;
                Add(statements, text, start, end, model);
                start = end;
            }
        }

        Add(statements, text, start, text.Length, model);
        return statements;
    }

    /// <summary>True when the text holds nothing but whitespace, comments and semicolons.</summary>
    public static bool IsBlank(string segment, StudioModel model)
    {
        TokenLexer lexer = Lex(segment, model);
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type is not (TokenType.Comment or TokenType.Semicolon))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The first word of a statement, skipping comments and opening parentheses (upper-case), or
    /// an empty string when the statement starts with anything else.
    /// </summary>
    public static string FirstKeyword(string statement, StudioModel model)
    {
        TokenLexer lexer = Lex(statement, model);
        while (lexer.MoveNext())
        {
            Token token = lexer.Current;
            if (token.Type is TokenType.Comment or TokenType.LeftParen)
            {
                continue;
            }

            // No keyword list is passed to the lexer, so every word is an Identifier.
            return token.Type == TokenType.Identifier ? token.Value.ToString().ToUpperInvariant() : string.Empty;
        }

        return string.Empty;
    }

    private static void Add(List<ScriptStatement> statements, string text, int start, int end, StudioModel model)
    {
        if (end <= start)
        {
            return;
        }

        string segment = text[start..end];
        if (IsBlank(segment, model))
        {
            return;
        }

        int leading = 0;
        while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
        {
            leading++;
        }

        statements.Add(new ScriptStatement(segment.Trim(), start + leading));
    }

    // Splitting needs token boundaries, never a word's role. Only GQL recognizes left arrows;
    // enabling them for SQL/OQL would consume the start of a valid -- comment after < (#1647).
    private static TokenLexer Lex(string text, StudioModel model) => new(text, new TokenLexerOptions
    {
        Keywords = ReadOnlySpan<string>.Empty,
        RecognizeLeftArrow = model == StudioModel.Graph,
    });
}
