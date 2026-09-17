using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public class GqlLanguageProfileTests
{
    [Fact]
    public void Instance_DeclaresIsoGqlLexicalSurface()
    {
        var profile = GqlLanguageProfile.Instance;

        profile.Language.ShouldBe("GQL");
        profile.IsCaseSensitive.ShouldBeFalse();
        profile.Keywords.ToArray().ShouldContain("MATCH");
        profile.Functions.ToArray().ShouldContain("elementId");
    }

    [Theory]
    [InlineData(GqlClauses.Match)]
    [InlineData(GqlClauses.OptionalMatch)]
    [InlineData(GqlClauses.MandatoryMatch)]
    [InlineData(GqlClauses.Return)]
    [InlineData(GqlClauses.Create)]
    [InlineData(GqlClauses.Insert)]
    [InlineData(GqlClauses.Delete)]
    [InlineData(GqlClauses.Merge)]
    [InlineData(GqlClauses.Where)]
    [InlineData(GqlClauses.OrderBy)]
    [InlineData(GqlClauses.SetOperation)]
    [InlineData(GqlClauses.Call)]
    public void Instance_DeclaredClause_IsSupported(string clause)
    {
        GqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();
    }

    [Fact]
    public void Instance_LowercaseClause_IsSupported()
    {
        GqlLanguageProfile.Instance.Supports("optional match").ShouldBeTrue();
    }

    [Fact]
    public void ToLexerOptions_PreservesGqlTokenClassification()
    {
        var lexer = new TokenLexer(
            "match COUNT",
            GqlLanguageProfile.Instance.ToLexerOptions());
        var tokenTypes = new List<TokenType>();

        foreach (var token in lexer)
        {
            tokenTypes.Add(token.Type);
        }

        tokenTypes.ShouldBe([TokenType.Keyword, TokenType.Function]);
    }
}
