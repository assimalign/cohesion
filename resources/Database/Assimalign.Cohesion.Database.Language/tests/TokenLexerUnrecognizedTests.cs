using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

/// <summary>
/// A character no language uses lexes as <see cref="TokenType.Unrecognized"/>, never as an
/// identifier, so no parser can bind it as a name (#1101).
/// </summary>
public sealed class TokenLexerUnrecognizedTests
{
    /// <summary>Each stray character is one token at its own span, between ordinary tokens.</summary>
    /// <param name="character">The character outside every language.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: a character no language uses is one Unrecognized token")]
    [InlineData("?")]
    [InlineData("#")]
    [InlineData("^")]
    [InlineData("§")]
    [InlineData("`")]
    [InlineData("\\")]
    [InlineData("​")]
    [InlineData("٣")] // ARABIC-INDIC DIGIT THREE
    [InlineData("１")] // FULLWIDTH DIGIT ONE
    [InlineData("३")] // DEVANAGARI DIGIT THREE
    public void MoveNext_StrayCharacter_ShouldLexAsOneUnrecognizedToken(string character)
    {
        // Act
        var tokens = Lex($"a {character} b");

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.Identifier, "a", 0),
            (TokenType.Unrecognized, character, 2),
            (TokenType.Identifier, "b", 4),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Lexer: adjacent stray characters are separate tokens")]
    public void MoveNext_AdjacentStrayCharacters_ShouldLexOneTokenEach()
    {
        // Act
        var tokens = Lex("t?#^§");

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.Identifier, "t", 0),
            (TokenType.Unrecognized, "?", 1),
            (TokenType.Unrecognized, "#", 2),
            (TokenType.Unrecognized, "^", 3),
            (TokenType.Unrecognized, "§", 4),
        ]);
    }

    /// <summary>
    /// <c>char.IsDigit</c> accepted every Unicode decimal digit, so <c>٣</c> lexed as an Integer
    /// that SQL parsed cleanly and the engine could not convert. Only ASCII digits make a
    /// number; a non-ASCII digit inside a name stays part of the identifier.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Language] - Lexer: only ASCII digits make a numeric literal")]
    public void MoveNext_NonAsciiDigits_ShouldNotLexAsNumbers()
    {
        // Act
        var tokens = Lex("1٣ a٣ .٣ 12.5e3");

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.Integer, "1", 0),
            (TokenType.Unrecognized, "٣", 1),
            (TokenType.Identifier, "a٣", 3),
            (TokenType.Dot, ".", 6),
            (TokenType.Unrecognized, "٣", 7),
            (TokenType.Float, "12.5e3", 9),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Lexer: a supplementary character is one Unrecognized token")]
    public void MoveNext_SurrogatePair_ShouldLexAsOneToken()
    {
        // Act
        var tokens = Lex("x \U0001F643 y");

        // Assert
        tokens.ShouldBe(
        [
            (TokenType.Identifier, "x", 0),
            (TokenType.Unrecognized, "\U0001F643", 2),
            (TokenType.Identifier, "y", 5),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Lexer: Unrecognized is appended after Eof")]
    public void TokenType_Unrecognized_ShouldFollowEofWithoutRenumbering()
    {
        // Assert: appending keeps every existing member's value, so persisted or compared
        // values from earlier builds still mean the same token kind.
        ((int)TokenType.Unrecognized).ShouldBe((int)TokenType.Eof + 1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: characters every language uses are not Unrecognized")]
    [InlineData("~", TokenType.Tilde)]
    [InlineData("!", TokenType.Bang)]
    [InlineData("%", TokenType.Percent)]
    [InlineData("|", TokenType.Pipe)]
    [InlineData("&", TokenType.Ampersand)]
    [InlineData("$1", TokenType.Parameter)]
    [InlineData("@p", TokenType.Parameter)]
    [InlineData("-- line comment", TokenType.Comment)]
    public void MoveNext_RecognizedCharacter_ShouldKeepItsTokenType(string text, TokenType type)
    {
        // Act
        var tokens = Lex(text);

        // Assert: -- stays a line comment in every language, including GQL.
        tokens.ShouldBe([(type, text, 0)]);
    }

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
