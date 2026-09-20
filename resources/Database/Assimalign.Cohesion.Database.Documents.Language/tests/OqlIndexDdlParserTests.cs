using System.Linq;
using Assimalign.Cohesion.Database.Language;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlIndexDdlParserTests
{
    [Fact]
    public void CreateIndex_ParsesNameCollectionAndNestedDocumentPath()
    {
        const string query = "CREATE INDEX ix_postal ON people (addresses[0]['postal-code']);";

        var statement = Parse(query);
        var create = statement.OqlExpression.ShouldBeOfType<OqlCreateIndexExpression>();

        statement.Diagnostics.ShouldBeEmpty();
        statement.Expression.ShouldBeSameAs(create);
        create.Text.ShouldBe(query);
        create.IndexName.ShouldBe("ix_postal");
        create.Collection.ShouldBe("people");
        create.Path.Segments.ShouldBe(new[]
        {
            new OqlPathSegment("addresses", null),
            new OqlPathSegment(null, 0),
            new OqlPathSegment("postal-code", null),
        });
        create.Location!.Start.ShouldBe(0);
        create.Location.End.ShouldBe(query.Length - 1);
        create.Path.Location!.Start.ShouldBe(34);
        create.Path.Location.End.ShouldBe(61);
    }

    [Fact]
    public void CreateIndex_AcceptsTheWhereClauseDocumentPathGrammar()
    {
        const string query = "create index \"ix city\" on \"person collection\" (\"home address\".city);";

        var create = Parse(query).OqlExpression.ShouldBeOfType<OqlCreateIndexExpression>();

        create.IndexName.ShouldBe("ix city");
        create.Collection.ShouldBe("person collection");
        create.Path.Segments.ShouldBe(new[]
        {
            new OqlPathSegment("home address", null),
            new OqlPathSegment("city", null),
        });
    }

    [Fact]
    public void DropIndex_ParsesNameAndCollection()
    {
        const string query = "DROP INDEX ix_postal ON people";

        var statement = Parse(query);
        var drop = statement.OqlExpression.ShouldBeOfType<OqlDropIndexExpression>();

        statement.Diagnostics.ShouldBeEmpty();
        drop.Text.ShouldBe(query);
        drop.IndexName.ShouldBe("ix_postal");
        drop.Collection.ShouldBe("people");
        drop.Location!.Start.ShouldBe(0);
        drop.Location.End.ShouldBe(query.Length);
    }

    [Theory]
    [InlineData("CREATE INDEX", 12)]
    [InlineData("CREATE INDEX ix people (name)", 16)]
    [InlineData("CREATE INDEX ix ON (name)", 19)]
    [InlineData("CREATE INDEX ix ON people name)", 26)]
    [InlineData("CREATE INDEX ix ON people ()", 27)]
    [InlineData("CREATE INDEX ix ON people (name", 31)]
    [InlineData("CREATE INDEX ix ON people (name + 1)", 32)]
    [InlineData("DROP INDEX", 10)]
    [InlineData("DROP INDEX ix people", 14)]
    [InlineData("DROP INDEX ix ON", 16)]
    public void MalformedIndexDdl_ReturnsStableLocatedDiagnostic(string query, int expectedStart)
    {
        var diagnostics = Parse(query).Diagnostics.ToArray();

        diagnostics.ShouldNotBeEmpty();
        var diagnostic = diagnostics.First(item => item.Code == "OQL0002");
        diagnostic.Start.ShouldBe(expectedStart);
        diagnostic.End!.Value.ShouldBeInRange(expectedStart, query.Length);
        diagnostic.Line.ShouldBe(1);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
    }

    [Fact]
    public void MalformedIndexDdl_OnSecondLineReportsAbsoluteLocation()
    {
        const string query = "CREATE INDEX ix\npeople (address.city)";

        var diagnostic = Parse(query).Diagnostics.First(item => item.Code == "OQL0002");

        diagnostic.Start.ShouldBe(16);
        diagnostic.End.ShouldBe(22);
        diagnostic.Line.ShouldBe(2);
    }

    [Theory]
    [InlineData("CREATE TABLE people", "CREATE", 0)]
    [InlineData("DROP COLLECTION people", "DROP", 0)]
    [InlineData("ALTER INDEX ix", "ALTER", 0)]
    public void UnsupportedClause_StillReportsCohdbl001(string query, string clause, int start)
    {
        var diagnostic = Parse(query).Diagnostics.First(item => item.Code == "COHDBL001");

        diagnostic.Message!.ShouldContain(clause);
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(start + clause.Length);
    }

    private static OqlQueryStatement Parse(string query) =>
        new OqlQueryParser().Parse(query).ShouldBeOfType<OqlQueryStatement>();
}
