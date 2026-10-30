using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlOperatorLexerConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Lexer: SQL operator additions preserve shared token boundaries (#1721)")]
    [InlineData("::", new[] { TokenType.ColonColon })]
    [InlineData(": :", new[] { TokenType.Colon, TokenType.Colon })]
    [InlineData(":::", new[] { TokenType.ColonColon, TokenType.Colon })]
    [InlineData("::::", new[] { TokenType.ColonColon, TokenType.ColonColon })]
    [InlineData("#", new[] { TokenType.Unrecognized })]
    [InlineData("~", new[] { TokenType.Tilde })]
    [InlineData("&", new[] { TokenType.Ampersand })]
    [InlineData("|", new[] { TokenType.Pipe })]
    [InlineData("<<", new[] { TokenType.LessThan, TokenType.LessThan })]
    [InlineData(">>", new[] { TokenType.GreaterThan, TokenType.GreaterThan })]
    [InlineData("->", new[] { TokenType.RightArrow })]
    [InlineData("->>", new[] { TokenType.RightArrow, TokenType.GreaterThan })]
    [InlineData("-> >", new[] { TokenType.RightArrow, TokenType.GreaterThan })]
    [InlineData("~~*", new[] { TokenType.Tilde, TokenType.Tilde, TokenType.Asterisk })]
    [InlineData("!~~*", new[] { TokenType.Bang, TokenType.Tilde, TokenType.Tilde, TokenType.Asterisk })]
    public void Tokenize_OperatorSequences_ShouldRetainTokenTypes(string text, TokenType[] expected)
    {
        // Arrange
        var lexer = new TokenLexer(text, SqlLanguageProfile.Instance.ToLexerOptions());
        var types = new List<TokenType>();
        // Act
        foreach (var token in lexer) { types.Add(token.Type); }
        // Assert
        types.ShouldBe(expected);
    }
}
