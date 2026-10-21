using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

public sealed class TokenLexerDelimitedIdentifierTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: doubled identifier quotes stay in one token with source offsets")]
    [InlineData("\"a\"\"b\"")]
    [InlineData("\"\"\"\"")]
    [InlineData("\"a\"\"\"\"b\"")]
    [InlineData("\"a\n\"\"b\"")]
    [InlineData("\"a\"\"")]
    public void MoveNext_DoubledQuote_ShouldPreserveSingleLexeme(string quoted)
    {
        // Arrange
        var lexer = new TokenLexer(" " + quoted, new TokenLexerOptions { Keywords = [] });
        var tokens = new List<(TokenType Type, string Text, int Position)>();

        // Act
        foreach (var token in lexer) { tokens.Add((token.Type, token.Value.ToString(), token.Position)); }

        // Assert: termination belongs to each parser; even a truncated token retains its full span.
        tokens.ShouldBe([(TokenType.QuotedIdentifier, quoted, 1)]);
    }
}
