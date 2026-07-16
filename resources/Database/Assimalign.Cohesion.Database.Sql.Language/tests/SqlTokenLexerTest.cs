using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Lexer smoke tests over the declared SQL token tables.
/// </summary>
public class SqlTokenLexerTest
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Lexer: tokenizes keywords, functions, and qualified names")]
    public void MoveNext_SimpleQuery_ShouldClassifyTokens()
    {
        // Arrange
        var lexer = new TokenLexer("SELECT Count(*) FROM dbo.Users", TokenLexerOptions.Sql);
        var tokens = new List<(string Value, TokenType Type)>();

        // Act
        while (lexer.MoveNext())
        {
            tokens.Add((lexer.Current.Value.ToString(), lexer.Current.Type));
        }

        // Assert
        tokens.ShouldBe(new[]
        {
            ("SELECT", TokenType.Keyword),
            ("Count", TokenType.Function),
            ("(", TokenType.LeftParen),
            ("*", TokenType.Asterisk),
            (")", TokenType.RightParen),
            ("FROM", TokenType.Keyword),
            ("dbo", TokenType.Identifier),
            (".", TokenType.Dot),
            ("Users", TokenType.Identifier),
        });
    }
}
