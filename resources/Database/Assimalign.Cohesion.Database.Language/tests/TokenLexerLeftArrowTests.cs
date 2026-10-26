using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

public sealed class TokenLexerLeftArrowTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Language] - Lexer: left arrows require an explicit profile option (#1647)")]
    [InlineData(false, "a<-1", "Identifier:a@0 LessThan:<@1 Minus:-@2 Integer:1@3")]
    [InlineData(false, "a <- 1", "Identifier:a@0 LessThan:<@2 Minus:-@3 Integer:1@5")]
    [InlineData(false, "a<--1", "Identifier:a@0 LessThan:<@1 Comment:--1@2")]
    [InlineData(true, "a<-1", "Identifier:a@0 LeftArrow:<-@1 Integer:1@3")]
    [InlineData(true, "a<--1", "Identifier:a@0 LeftArrow:<-@1 Minus:-@3 Integer:1@4")]
    [InlineData(false, "<= <> ->", "LessEqual:<=@0 NotEquals:<>@3 RightArrow:->@6")]
    [InlineData(true, "<= <> ->", "LessEqual:<=@0 NotEquals:<>@3 RightArrow:->@6")]
    public void MoveNext_LeftArrowOption_ShouldPreserveTokensAndOffsets(bool enabled, string source, string expected)
    {
        // Arrange
        var profile = new QueryLanguageProfile("TEST", [], [], [], recognizeLeftArrow: enabled);
        var lexer = new TokenLexer(source, profile.ToLexerOptions());
        var tokens = new List<string>();

        // Act
        foreach (var token in lexer)
        {
            tokens.Add($"{token.Type}:{token.Value.ToString()}@{token.Position}");
        }

        // Assert
        string.Join(' ', tokens).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Language] - Lexer: a default profile splits less-than and minus (#1647)")]
    public void ToLexerOptions_DefaultProfile_ShouldDisableLeftArrow()
    {
        // Arrange
        var profile = new QueryLanguageProfile("TEST", [], [], []);

        // Act
        var options = profile.ToLexerOptions();

        // Assert
        profile.RecognizeLeftArrow.ShouldBeFalse();
        options.RecognizeLeftArrow.ShouldBeFalse();
        new TokenLexerOptions { Keywords = [] }.RecognizeLeftArrow.ShouldBeFalse();
    }
}
