using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>A statement cut out of the editor text, with its offset in that text.</summary>
internal readonly record struct ScriptStatement(string Text, int Offset);

/// <summary>
/// Splits editor text into statements on <c>;</c> outside quotes and comments. Every engine parses
/// one statement per call (SQL, OQL and GQL all reject multiple statements), so the Studio runs a
/// script statement by statement. The terminating <c>;</c> stays with its statement. A line
/// comment ends where the engines' shared lexer ends one (<see cref="TokenLexer.IsLineTerminator(char)"/>,
/// #1150). The Windows editor control separates lines with CR, so a comment that ran to the next LF
/// hid every statement after it.
/// </summary>
internal static class ScriptSplitter
{
    public static List<ScriptStatement> Split(string text)
    {
        var statements = new List<ScriptStatement>();
        int start = 0;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(text, i, c);
                continue;
            }

            if ((c == '-' && next == '-') || (c == '/' && next == '/'))
            {
                i = SkipLineComment(text, i);
                continue;
            }

            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }

            if (c == ';')
            {
                Add(statements, text, start, i + 1);
                start = i + 1;
            }

            i++;
        }

        Add(statements, text, start, text.Length);
        return statements;
    }

    /// <summary>
    /// The index of the line terminator that ends the comment starting at <paramref name="index"/>,
    /// or the text length when none follows. The terminator is whitespace to every caller.
    /// </summary>
    private static int SkipLineComment(string text, int index)
    {
        int i = index + 2;
        while (i < text.Length && !TokenLexer.IsLineTerminator(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int SkipQuoted(string text, int index, char quote)
    {
        int i = index + 1;
        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                // A doubled quote is an escaped quote in every Cohesion dialect.
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static void Add(List<ScriptStatement> statements, string text, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        string segment = text[start..end];
        if (IsBlank(segment))
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

    /// <summary>True when the text holds nothing but whitespace, comments and semicolons.</summary>
    public static bool IsBlank(string segment)
    {
        int i = 0;
        while (i < segment.Length)
        {
            char c = segment[i];
            char next = i + 1 < segment.Length ? segment[i + 1] : '\0';
            if (char.IsWhiteSpace(c) || c == ';')
            {
                i++;
            }
            else if ((c == '-' && next == '-') || (c == '/' && next == '/'))
            {
                i = SkipLineComment(segment, i);
            }
            else if (c == '/' && next == '*')
            {
                int end = segment.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? segment.Length : end + 2;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first keyword of a statement, skipping comments and parentheses (upper-case).</summary>
    public static string FirstKeyword(string statement)
    {
        int i = 0;
        while (i < statement.Length)
        {
            char c = statement[i];
            char next = i + 1 < statement.Length ? statement[i + 1] : '\0';
            if (char.IsWhiteSpace(c) || c == '(')
            {
                i++;
            }
            else if ((c == '-' && next == '-') || (c == '/' && next == '/'))
            {
                i = SkipLineComment(statement, i);
            }
            else if (c == '/' && next == '*')
            {
                int end = statement.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? statement.Length : end + 2;
            }
            else
            {
                int start = i;
                while (i < statement.Length && (char.IsLetter(statement[i]) || statement[i] == '_'))
                {
                    i++;
                }

                return statement[start..i].ToUpperInvariant();
            }
        }

        return string.Empty;
    }
}
