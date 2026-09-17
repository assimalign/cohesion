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
        profile.Functions.ToArray().ShouldContain("COUNT");
    }

    [Theory]
    [InlineData(OqlClauses.Select)]
    [InlineData(OqlClauses.From)]
    [InlineData(OqlClauses.Where)]
    [InlineData(OqlClauses.GroupBy)]
    [InlineData(OqlClauses.Having)]
    [InlineData(OqlClauses.OrderBy)]
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
    [InlineData(OqlClauses.Define)]
    [InlineData(OqlClauses.Element)]
    [InlineData(OqlClauses.Flatten)]
    [InlineData(OqlClauses.Subquery)]
    public void Instance_UnimplementedClause_IsNotSupported(string clause)
    {
        OqlLanguageProfile.Instance.Supports(clause).ShouldBeFalse();
    }
}
