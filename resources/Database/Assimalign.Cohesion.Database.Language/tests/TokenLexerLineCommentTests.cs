using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

/// <summary>
/// A <c>--</c> line comment ends at the first line terminator: LF, CR, NEL (U+0085), LINE
/// SEPARATOR (U+2028) or PARAGRAPH SEPARATOR (U+2029). It used to end only at LF, so the text
/// after a lone CR stayed comment text and <c>DELETE FROM t -- c&lt;CR&gt;WHERE id = 1</c> deleted
/// every row (#1150).
/// </summary>
public sealed class TokenLexerLineCommentTests
{
    /// <summary>The comment token stops before the terminator, and the next line lexes as code.</summary>
    /// <param name="terminator">The line terminator, by name (see <see cref="Terminator"/>).</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: a line comment ends at every line terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void MoveNext_LineCommentBeforeTerminator_ShouldEndAtTheTerminator(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);
        string text = "DELETE FROM t -- note" + separator + "WHERE id = 1";
        int where = text.IndexOf("WHERE", StringComparison.Ordinal);

        // Act
        var tokens = Lex(text);

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.Identifier, "DELETE", 0),
            (TokenType.Identifier, "FROM", 7),
            (TokenType.Identifier, "t", 12),
            (TokenType.Comment, "-- note", 14),
            (TokenType.Identifier, "WHERE", where),
            (TokenType.Identifier, "id", where + 6),
            (TokenType.Equals, "=", where + 9),
            (TokenType.Integer, "1", where + 11),
        ]);
    }

    /// <summary>An empty comment ends at the terminator right after its <c>--</c>.</summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: an empty line comment ends at the next terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void MoveNext_EmptyLineComment_ShouldEndAtTheTerminator(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);

        // Act
        var tokens = Lex("--" + separator + "b");

        // Assert
        tokens.ShouldBe([(TokenType.Comment, "--", 0), (TokenType.Identifier, "b", 2 + separator.Length)]);
    }

    /// <summary>
    /// A comment with no terminator after it runs to the end of the input; one whose
    /// terminator is the last character leaves nothing after it.
    /// </summary>
    /// <param name="text">The input.</param>
    /// <param name="comment">The comment token's text.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: a line comment at the end of the input")]
    [InlineData("a -- trailing", "-- trailing")]
    [InlineData("a --", "--")]
    [InlineData("a -- trailing\r", "-- trailing")]
    [InlineData("a -- trailing\r\n", "-- trailing")]
    [InlineData("a -- trailing\n", "-- trailing")]
    public void MoveNext_LineCommentAtEndOfInput_ShouldBeTheLastToken(string text, string comment)
    {
        // Act
        var tokens = Lex(text);

        // Assert
        tokens.ShouldBe([(TokenType.Identifier, "a", 0), (TokenType.Comment, comment, 2)]);
    }

    /// <summary>Each terminator ends one comment, so consecutive comments are separate tokens.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Language] - Lexer: consecutive comments end at their own terminators")]
    public void MoveNext_ConsecutiveLineComments_ShouldEachEndAtTheirTerminator()
    {
        // Arrange
        string text = "-- one\r-- two" + Terminator("NEL") + "-- three" + Terminator("LS") + "x" + Terminator("PS") + "-- four";

        // Act
        var tokens = Lex(text);

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.Comment, "-- one", 0),
            (TokenType.Comment, "-- two", 7),
            (TokenType.Comment, "-- three", 14),
            (TokenType.Identifier, "x", 23),
            (TokenType.Comment, "-- four", 25),
        ]);
    }

    /// <summary>
    /// The escape spellings <c>\r</c>, <c>\n</c> and <c>\u2028</c> are text, not line
    /// terminators: inside a comment they do not end it, and inside a string literal on the
    /// same line neither the escape nor a <c>--</c> starts or ends a comment.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Language] - Lexer: an escape spelling of a terminator does not end a comment")]
    public void MoveNext_TerminatorEscapeSpelling_ShouldNotEndAComment()
    {
        // Arrange: every backslash below is a literal backslash in the query text.
        const string text = @"SELECT 'a\r--b\n' -- see '\r\n' and \u2028 here" + "\rWHERE";

        // Act
        var tokens = Lex(text);

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.Identifier, "SELECT", 0),
            (TokenType.String, @"'a\r--b\n'", 7),
            (TokenType.Comment, @"-- see '\r\n' and \u2028 here", 18),
            (TokenType.Identifier, "WHERE", 48),
        ]);
    }

    /// <summary>
    /// A string literal may span lines, so a terminator inside it ends nothing and a <c>--</c>
    /// inside it starts no comment; the comment after the literal still ends at its own terminator.
    /// </summary>
    /// <param name="terminator">The line terminator inside the literal and after the comment, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: a terminator inside a string literal is literal text")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void MoveNext_TerminatorInsideStringLiteral_ShouldStayInTheLiteral(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);
        string literal = "'a" + separator + "-- b'";
        string text = literal + " -- c" + separator + "WHERE";

        // Act
        var tokens = Lex(text);

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.String, literal, 0),
            (TokenType.Comment, "-- c", literal.Length + 1),
            (TokenType.Identifier, "WHERE", literal.Length + 6),
        ]);
    }

    /// <summary>
    /// A quoted identifier may span lines, so a terminator inside it ends nothing and a <c>--</c>
    /// inside it starts no comment.
    /// </summary>
    /// <param name="terminator">The line terminator inside the quoted identifier, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: a terminator inside a quoted identifier is part of the name")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void MoveNext_TerminatorInsideQuotedIdentifier_ShouldStayInTheIdentifier(string terminator)
    {
        // Arrange
        string quoted = "\"q" + Terminator(terminator) + "--x\"";

        // Act
        var tokens = Lex(quoted + " WHERE");

        // Assert
        tokens.ShouldBe([(TokenType.QuotedIdentifier, quoted, 0), (TokenType.Identifier, "WHERE", quoted.Length + 1)]);
    }

    /// <summary>
    /// A block comment may span lines, so a terminator inside it ends nothing and a <c>--</c>
    /// inside it starts no line comment: the block comment still ends at its <c>*/</c>.
    /// </summary>
    /// <param name="terminator">The line terminator inside the block comment, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: a terminator inside a block comment ends nothing")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void MoveNext_TerminatorInsideBlockComment_ShouldStayInTheComment(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);
        string block = "/* a" + separator + "-- b" + separator + "*/";

        // Act
        var tokens = Lex(block + " WHERE");

        // Assert
        tokens.ShouldBe([(TokenType.Comment, block, 0), (TokenType.Identifier, "WHERE", block.Length + 1)]);
    }

    /// <summary>
    /// A <c>/*</c> inside a line comment opens no block comment, so the line comment still ends
    /// at its terminator and the next line lexes as code.
    /// </summary>
    /// <param name="terminator">The line terminator after the comment, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: /* inside a line comment opens nothing")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void MoveNext_BlockCommentOpenerInsideLineComment_ShouldEndAtTheTerminator(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);

        // Act
        var tokens = Lex("-- a /* b" + separator + "WHERE");

        // Assert
        tokens.ShouldBe([(TokenType.Comment, "-- a /* b", 0), (TokenType.Identifier, "WHERE", 9 + separator.Length)]);
    }

    /// <summary>
    /// Vertical tab and form feed are whitespace but not line terminators, as in PostgreSQL and
    /// C#, so they stay inside a comment.
    /// </summary>
    /// <param name="whitespace">The whitespace character.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: vertical tab and form feed do not end a comment")]
    [InlineData("\v")]
    [InlineData("\f")]
    [InlineData("\t")]
    public void MoveNext_NonTerminatorWhitespace_ShouldStayInTheComment(string whitespace)
    {
        // Act
        var tokens = Lex("-- c" + whitespace + "WHERE");

        // Assert
        tokens.ShouldBe([(TokenType.Comment, "-- c" + whitespace + "WHERE", 0)]);
    }

    /// <summary>
    /// Outside a comment every terminator is whitespace that separates tokens, never an
    /// <see cref="TokenType.Unrecognized"/> character (#1101).
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: a terminator outside a comment separates tokens")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void MoveNext_TerminatorOutsideComment_ShouldSeparateTokens(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);

        // Act
        var tokens = Lex("a" + separator + "b");

        // Assert
        tokens.ShouldBe([(TokenType.Identifier, "a", 0), (TokenType.Identifier, "b", 1 + separator.Length)]);
    }

    /// <param name="value">The character, as a UTF-16 code unit.</param>
    /// <param name="expected">Whether it is a line terminator.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: the line terminators are LF, CR, NEL, LS and PS")]
    [InlineData(0x000A, true)]
    [InlineData(0x000D, true)]
    [InlineData(0x0085, true)]
    [InlineData(0x2028, true)]
    [InlineData(0x2029, true)]
    [InlineData(0x000B, false)]
    [InlineData(0x000C, false)]
    [InlineData(0x0009, false)]
    [InlineData(0x0020, false)]
    [InlineData(0x00A0, false)]
    [InlineData(0x200B, false)]
    [InlineData('n', false)]
    [InlineData('r', false)]
    public void IsLineTerminator_Character_ShouldMatchTheSharedSet(int value, bool expected)
    {
        // Act
        bool actual = TokenLexer.IsLineTerminator((char)value);

        // Assert
        actual.ShouldBe(expected);
    }

    /// <param name="text">The text, with terminators written by name in braces.</param>
    /// <param name="expected">The number of line breaks.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: CR LF is one line break and every other terminator is one")]
    [InlineData("", 0)]
    [InlineData("a", 0)]
    [InlineData("a{LF}b", 1)]
    [InlineData("a{CR}b", 1)]
    [InlineData("a{CRLF}b", 1)]
    [InlineData("a{NEL}b", 1)]
    [InlineData("a{LS}b", 1)]
    [InlineData("a{PS}b", 1)]
    [InlineData("a{CR}{CR}b", 2)]
    [InlineData("a{LF}{CR}b", 2)]
    [InlineData("a{CRLF}{LF}b", 2)]
    [InlineData("{CR}", 1)]
    [InlineData("{CRLF}", 1)]
    [InlineData("1{CRLF}2{CR}3{NEL}4{LS}5{PS}6{LF}", 6)]
    [InlineData("a\vb\fc\td", 0)]
    public void CountLineBreaks_Text_ShouldCountCrLfOnce(string text, int expected)
    {
        // Arrange
        string expanded = text
            .Replace("{CRLF}", Terminator("CRLF"), StringComparison.Ordinal)
            .Replace("{LF}", Terminator("LF"), StringComparison.Ordinal)
            .Replace("{CR}", Terminator("CR"), StringComparison.Ordinal)
            .Replace("{NEL}", Terminator("NEL"), StringComparison.Ordinal)
            .Replace("{LS}", Terminator("LS"), StringComparison.Ordinal)
            .Replace("{PS}", Terminator("PS"), StringComparison.Ordinal);

        // Act
        int actual = TokenLexer.CountLineBreaks(expanded);

        // Assert
        actual.ShouldBe(expected);
    }

    /// <summary>The line terminators by name, so no invisible character sits in the test source.</summary>
    /// <param name="name">LF, CR, CRLF, NEL, LS or PS.</param>
    /// <returns>The terminator text.</returns>
    private static string Terminator(string name) => name switch
    {
        "LF" => "\n",
        "CR" => "\r",
        "CRLF" => "\r\n",
        "NEL" => ((char)0x0085).ToString(),
        "LS" => ((char)0x2028).ToString(),
        "PS" => ((char)0x2029).ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown line terminator."),
    };

    private static List<(TokenType Type, string Value, int Position)> Lex(string text)
    {
        var tokens = new List<(TokenType, string, int)>();
        var lexer = new TokenLexer(text, new TokenLexerOptions { Keywords = [] });
        foreach (var token in lexer)
        {
            tokens.Add((token.Type, token.Value.ToString(), token.Position));
        }

        return tokens;
    }
}
