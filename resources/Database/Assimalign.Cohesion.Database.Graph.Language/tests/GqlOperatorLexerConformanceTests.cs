using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlOperatorLexerConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Lexer: SQL operator additions preserve shared token boundaries (#1721)")]
    [InlineData(":A|B", new[] { TokenType.Colon, TokenType.Identifier, TokenType.Pipe, TokenType.Identifier })]
    [InlineData(":A&B", new[] { TokenType.Colon, TokenType.Identifier, TokenType.Ampersand, TokenType.Identifier })]
    [InlineData("!", new[] { TokenType.Bang })]
    [InlineData("<-", new[] { TokenType.LeftArrow })]
    [InlineData("->", new[] { TokenType.RightArrow })]
    [InlineData("->>", new[] { TokenType.RightArrow, TokenType.GreaterThan })]
    [InlineData("<--", new[] { TokenType.LeftArrow, TokenType.Minus })]
    [InlineData("-->", new[] { TokenType.Comment })]
    [InlineData("<-[", new[] { TokenType.LeftArrow, TokenType.LeftBracket })]
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
    [InlineData("~~*", new[] { TokenType.Tilde, TokenType.Tilde, TokenType.Asterisk })]
    [InlineData("!~~*", new[] { TokenType.Bang, TokenType.Tilde, TokenType.Tilde, TokenType.Asterisk })]
    public void Tokenize_OperatorSequences_ShouldRetainTokenTypes(string text, TokenType[] expected)
    {
        // Arrange
        var lexer = new TokenLexer(text, GqlLanguageProfile.Instance.ToLexerOptions());
        var types = new List<TokenType>();
        // Act
        foreach (var token in lexer) { types.Add(token.Type); }
        // Assert
        types.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Conformance: SQL-only operator forms remain refused (#1721)")]
    [InlineData("MATCH (n::Person) RETURN n")]
    [InlineData("MATCH (n: :Person) RETURN n")]
    [InlineData("RETURN 1 # 2") ]
    [InlineData("RETURN ~1") ]
    [InlineData("RETURN 1 & 2") ]
    [InlineData("RETURN 1 | 2") ]
    [InlineData("RETURN 1 << 2") ]
    [InlineData("RETURN 1 >> 2") ]
    [InlineData("RETURN '{}' -> 'a'")]
    [InlineData("RETURN '{}' ->> 'a'")]
    [InlineData("RETURN 'a' ~~* 'A'")]
    public void Parse_SqlOperators_ShouldRemainRefused(string text)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Conformance: existing labels and arrows retain their grammar (#1721)")]
    [InlineData("MATCH (n:A|B) RETURN n")]
    [InlineData("MATCH (n:A&B) RETURN n")]
    [InlineData("MATCH (n:!A) RETURN n")]
    [InlineData("MATCH (a)<-[r]-(b) RETURN a")]
    [InlineData("MATCH (a)->(b) RETURN a")]
    public void Parse_ExistingColonGrammar_ShouldRemainAccepted(string text)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
    }
}
