using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public class OqlLanguageProfileTests
{
    [Fact]
    public void Instance_DeclaresOqlLexicalSurface()
    {
        var profile = OqlLanguageProfile.Instance;

        profile.Language.ShouldBe("OQL");
        profile.IsCaseSensitive.ShouldBeFalse();
        profile.Keywords.ToArray().ShouldContain("SELECT");
        profile.Keywords.ToArray().ShouldContain("CREATE");
        profile.Keywords.ToArray().ShouldContain("INDEX");
        profile.Keywords.ToArray().ShouldContain("DROP");
        profile.Keywords.ToArray().ShouldContain("ON");
        profile.Functions.ToArray().ShouldContain("COUNT");
        profile.Functions.ToArray().ShouldContain("META");
        foreach (string word in new[] { "KEY", "VALUES", "INTO", "NULLS", "INCLUDE" }) { profile.Keywords.ToArray().ShouldNotContain(word); }
    }

    [Theory]
    [InlineData(OqlClauses.Begin)]
    [InlineData(OqlClauses.Commit)]
    [InlineData(OqlClauses.Rollback)]
    [InlineData(OqlClauses.Insert)]
    [InlineData(OqlClauses.Upsert)]
    [InlineData(OqlClauses.Update)]
    [InlineData(OqlClauses.Unset)]
    [InlineData(OqlClauses.Delete)]
    [InlineData(OqlClauses.Returning)]
    [InlineData(OqlClauses.Values)]
    [InlineData(OqlClauses.Select)]
    [InlineData(OqlClauses.Distinct)]
    [InlineData(OqlClauses.SelectValue)]
    [InlineData(OqlClauses.ObjectConstructor)]
    [InlineData(OqlClauses.ArrayConstructor)]
    [InlineData(OqlClauses.ArrayComprehension)]
    [InlineData(OqlClauses.FirstComprehension)]
    [InlineData(OqlClauses.Explain)]
    [InlineData(OqlClauses.From)]
    [InlineData(OqlClauses.Unnest)]
    [InlineData(OqlClauses.LeftUnnest)]
    [InlineData(OqlClauses.Any)]
    [InlineData(OqlClauses.Every)]
    [InlineData(OqlClauses.Where)]
    [InlineData(OqlClauses.In)]
    [InlineData(OqlClauses.Between)]
    [InlineData(OqlClauses.Like)]
    [InlineData(OqlClauses.Case)]
    [InlineData(OqlClauses.GroupBy)]
    [InlineData(OqlClauses.GroupAs)]
    [InlineData(OqlClauses.Having)]
    [InlineData(OqlClauses.OrderBy)]
    [InlineData(OqlClauses.Limit)]
    [InlineData(OqlClauses.Offset)]
    [InlineData(OqlClauses.CreateCollection)]
    [InlineData(OqlClauses.DropCollection)]
    [InlineData(OqlClauses.CreateIndex)]
    [InlineData(OqlClauses.DropIndex)]
    public void Instance_DeclaredClause_IsSupported(string clause)
    {
        OqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();
    }

    [Fact]
    public void Instance_LowercaseClause_IsSupported()
    {
        OqlLanguageProfile.Instance.Supports("select").ShouldBeTrue();
    }

    [Theory]
    [InlineData(OqlClauses.Subquery)]
    public void Instance_UnimplementedClause_IsNotSupported(string clause)
    {
        OqlLanguageProfile.Instance.Supports(clause).ShouldBeFalse();
    }
}
