using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

public sealed class QueryLanguageProfileTests
{
    [Fact]
    public void Supports_WhenProfileIsCaseInsensitive_IgnoresClauseCase()
    {
        var profile = new QueryLanguageProfile("TEST", [], [], ["GROUP BY"]);

        profile.Supports("group by").ShouldBeTrue();
    }

    [Fact]
    public void Supports_WhenProfileIsCaseSensitive_RequiresClauseCase()
    {
        var profile = new QueryLanguageProfile("TEST", [], [], ["GROUP BY"], isCaseSensitive: true);

        profile.Supports("GROUP BY").ShouldBeTrue();
        profile.Supports("group by").ShouldBeFalse();
    }

    [Fact]
    public void ToLexerOptions_RoundTripsLexicalTablesAndCasePolicy()
    {
        string[] keywords = ["SELECT", "FROM"];
        string[] functions = ["COUNT", "SUM"];
        var profile = new QueryLanguageProfile(
            "TEST",
            keywords,
            functions,
            ["SELECT"],
            isCaseSensitive: true);

        var options = profile.ToLexerOptions();

        options.Keywords.ToArray().ShouldBe(keywords);
        options.Functions.ToArray().ShouldBe(functions);
        options.IsCaseSensitive.ShouldBeTrue();
    }

    [Fact]
    public void Supports_WhenClauseSetIsEmpty_SupportsNothing()
    {
        var profile = new QueryLanguageProfile("TEST", [], [], []);

        profile.Clauses.ShouldBeEmpty();
        profile.Supports("SELECT").ShouldBeFalse();
    }
}
