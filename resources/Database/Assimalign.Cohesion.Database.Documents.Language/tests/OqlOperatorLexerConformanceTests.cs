using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlOperatorLexerConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Lexer: SQL operator additions preserve shared token boundaries (#1721)")]
    [InlineData("::", new[] { TokenType.ColonColon })]
    [InlineData(": :", new[] { TokenType.Colon, TokenType.Colon })]
    [InlineData(":::", new[] { TokenType.ColonColon, TokenType.Colon })]
    [InlineData("::::", new[] { TokenType.ColonColon, TokenType.ColonColon })]
    [InlineData("~~*", new[] { TokenType.Tilde, TokenType.Tilde, TokenType.Asterisk })]
    [InlineData("!~~*", new[] { TokenType.Bang, TokenType.Tilde, TokenType.Tilde, TokenType.Asterisk })]
    public void Tokenize_OperatorSequences_ShouldRetainTokenTypes(string text, TokenType[] expected)
    {
        // Arrange
        var lexer = new TokenLexer(text, OqlLanguageProfile.Instance.ToLexerOptions());
        var types = new List<TokenType>();
        // Act
        foreach (var token in lexer) { types.Add(token.Type); }
        // Assert
        types.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Conformance: SQL-only operator forms remain refused (#1721)")]
    [InlineData("SELECT a::TEXT FROM t")]
    [InlineData("SELECT a: :TEXT FROM t")]
    [InlineData("SELECT a ~~* 'x' FROM t")]
    public void Parse_SqlOperators_ShouldRemainRefused(string text)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse(text);
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Conformance: existing label and member colons retain their grammar (#1721)")]
    public void Parse_ExistingColonGrammar_ShouldRemainAccepted()
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse("SELECT {x:1} AS o FROM t");
        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == Assimalign.Cohesion.Database.Types.DiagnosticSeverity.Error);
    }
}
