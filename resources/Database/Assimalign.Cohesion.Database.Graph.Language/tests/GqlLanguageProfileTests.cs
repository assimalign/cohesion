using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Language;

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
        profile.Functions.ToArray().ShouldBe(["element_id", "elementId", "labels", "type", "properties", "keys", "startNode", "endNode", "path_length", "length", "nodes", "edges", "relationships", "lower", "toLower", "upper", "toUpper", "trim", "substring", "replace", "char_length", "character_length", "size", "abs", "ceil", "ceiling", "floor", "round", "sqrt", "ln", "log", "mod", "toString", "toInteger", "toFloat", "toBoolean", "current_date", "current_timestamp", "cast", "reduce", "count", "sum", "avg", "min", "max", "collect_list", "collect"]);
    }

    [Theory]
    [InlineData(GqlClauses.Match)]
    [InlineData(GqlClauses.QuantifiedPath)]
    [InlineData(GqlClauses.PathMode)]
    [InlineData(GqlClauses.PathSelector)]
    [InlineData(GqlClauses.OptionalMatch)]
    [InlineData(GqlClauses.Explain)]
    [InlineData(GqlClauses.Return)]
    [InlineData(GqlClauses.GroupBy)]
    [InlineData(GqlClauses.OrderBy)]
    [InlineData(GqlClauses.Offset)]
    [InlineData(GqlClauses.Skip)]
    [InlineData(GqlClauses.Limit)]
    [InlineData(GqlClauses.Exists)]
    [InlineData(GqlClauses.Case)]
    [InlineData(GqlClauses.ListComprehension)]
    [InlineData(GqlClauses.ListSlice)]
    [InlineData(GqlClauses.Reduce)]
    [InlineData(GqlClauses.ListPredicate)]
    [InlineData(GqlClauses.Create)]
    [InlineData(GqlClauses.Insert)]
    [InlineData(GqlClauses.Merge)]
    [InlineData(GqlClauses.Delete)]
    [InlineData(GqlClauses.DetachDelete)]
    [InlineData(GqlClauses.Where)]
    [InlineData(GqlClauses.LabelExpression)]
    public void Instance_DeclaredClause_IsSupported(string clause)
    {
        GqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();
    }

    [Fact]
    public void Instance_LowercaseClause_IsSupported()
    {
        GqlLanguageProfile.Instance.Supports("detach delete").ShouldBeTrue();
    }

    [Fact]
    public void ToLexerOptions_PreservesGqlTokenClassification()
    {
        var lexer = new TokenLexer(
            "match RETURN",
            GqlLanguageProfile.Instance.ToLexerOptions());
        var tokenTypes = new List<TokenType>();

        foreach (var token in lexer)
        {
            tokenTypes.Add(token.Type);
        }

        tokenTypes.ShouldBe([TokenType.Keyword, TokenType.Keyword]);
    }

    [Fact]
    public void Profile_AdvertisesOnlyExecutableClauses()
    {
        GqlLanguageProfile.Instance.Clauses.ShouldBe([
            GqlClauses.Match, GqlClauses.QuantifiedPath, GqlClauses.PathMode, GqlClauses.PathSelector, GqlClauses.OptionalMatch, GqlClauses.Explain, GqlClauses.Return, GqlClauses.SetOperation, GqlClauses.UnionDistinct, GqlClauses.UnionAll, GqlClauses.Next, GqlClauses.With, GqlClauses.GroupBy, GqlClauses.OrderBy, GqlClauses.Offset, GqlClauses.Skip, GqlClauses.Limit, GqlClauses.Exists, GqlClauses.Case, GqlClauses.ListComprehension, GqlClauses.PatternComprehension, GqlClauses.ListSlice, GqlClauses.Reduce, GqlClauses.ListPredicate, GqlClauses.Create, GqlClauses.Insert, GqlClauses.Merge,
            GqlClauses.Set, GqlClauses.Remove, GqlClauses.Delete, GqlClauses.DetachDelete, GqlClauses.Where, GqlClauses.Filter, GqlClauses.Let, GqlClauses.For, GqlClauses.Unwind, GqlClauses.Show,
            GqlClauses.LabelExpression]);
    }

    [Theory]
    [InlineData(GqlClauses.MandatoryMatch)]
    [InlineData(GqlClauses.Call)]
    public void FutureClauses_AreNotAdvertised(string clause)
    {
        GqlLanguageProfile.Instance.Supports(clause).ShouldBeFalse();
    }
}
