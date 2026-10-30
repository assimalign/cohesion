using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlOperatorLexerConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Lexer: SQL operator additions preserve shared token boundaries (#1721)")]
    [InlineData("::", new[] { TokenType.ColonColon })]
    [InlineData(": :", new[] { TokenType.Colon, TokenType.Colon })]
    [InlineData(":::", new[] { TokenType.ColonColon, TokenType.Colon })]
    [InlineData("::::", new[] { TokenType.ColonColon, TokenType.ColonColon })]
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
    [InlineData("RETURN 'a' ~~* 'A'")]
    public void Parse_SqlOperators_ShouldRemainRefused(string text)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Conformance: existing label and member colons retain their grammar (#1721)")]
    public void Parse_ExistingColonGrammar_ShouldRemainAccepted()
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse("MATCH (n:Person:Employee) RETURN n");
        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
    }
}
