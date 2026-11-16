using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentMergeWindowIntegrationTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE window source renders, explains and refuses execution before reads (#1804, #1805)")]
    [InlineData("ROW_NUMBER() OVER (PARTITION BY s.p ORDER BY s.n)", "")]
    [InlineData("ROW_NUMBER() OVER w", " WINDOW w AS (PARTITION BY s.p ORDER BY s.n)")]
    public async Task ExecuteAsync_MergeWindowSource_ShouldExplainAndRefuseWithoutReads(string expression, string window)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("merge-windows", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION targets", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION sources", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO targets (KEY, VALUE) VALUES ('a', {n:1})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO sources (KEY, VALUE) VALUES ('a', {k:'a',p:1,n:2})", cancellationToken: CancellationToken.None);
        string sql = $"MERGE INTO targets t USING (SELECT s.k AS k,{expression} AS n FROM sources s{window}) q ON KEY q.k WHEN MATCHED THEN UPDATE SET t.n=q.n";
        var parsed = DocumentQueryRequest.FromOql(sql).Statement.OqlExpression.ShouldBeOfType<OqlMergeExpression>();
        string canonical = parsed.ToString();
        string expectedExpression = window.Length == 0 ? "\"ROW_NUMBER\"() OVER (PARTITION BY \"s\".\"p\" ORDER BY \"s\".\"n\" ASC)" : "\"ROW_NUMBER\"() OVER \"w\"";
        string expectedWindow = window.Length == 0 ? "" : " WINDOW \"w\" AS (PARTITION BY \"s\".\"p\" ORDER BY \"s\".\"n\" ASC)";
        canonical.ShouldBe($"MERGE INTO \"targets\" AS \"t\" USING (SELECT \"s\".\"k\" AS \"k\", {expectedExpression} AS \"n\" FROM \"sources\" AS \"s\"{expectedWindow}) AS \"q\" ON KEY \"q\".\"k\" WHEN MATCHED THEN UPDATE SET \"t\".\"n\" = \"q\".\"n\"");

        // Act.
        DocumentQueryRequest.FromOql(canonical).Statement.OqlExpression.ToString().ShouldBe(canonical);
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + canonical);

        // Assert.
        plan.ShouldContain(row => row.GetString(2) == "Merge");
        plan.ShouldContain(row => row.GetString(2) == "WindowAggregate");
        plan.Single(row => row.GetString(2) == "WindowAggregate").GetString(5).ShouldNotBeNull().ShouldContain("functions=ROW_NUMBER", Case.Sensitive);
        string detail = plan.Single(row => row.GetString(2) == "WindowAggregate").GetString(5)!;
        // WindowKeyDetail uses the same canonical identifier quoting as MERGE.
        detail.ShouldContain("partition=[\"s\".\"p\"]", Case.Sensitive);
        detail.ShouldContain("order=[\"s\".\"n\" ASC NULLS FIRST]", Case.Sensitive);
        session.LastQueryDocumentsRead.ShouldBe(0);
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(canonical, cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05022");
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQueryKeyLookups.ShouldBe(0);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - MERGE application window-name calls retain reserved-name refusal (#1804, #1805)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Add_MergeApplicationWindowName_ShouldRefuseRegistration(bool aggregate)
    {
        // Arrange: both application function kinds were admitted at batch 4.
        var builder = DocumentDatabaseEngine.CreateBuilder("merge-reserved-window");
        string sql = "MERGE INTO targets t USING (SELECT ROW_NUMBER(s.n) AS k FROM sources s) q ON KEY q.k WHEN MATCHED THEN DELETE";

        // Act / Assert: registration refuses before any MERGE source can run.
        Should.Throw<System.ArgumentException>(() =>
        {
            if (aggregate) { builder.Functions.Add(DocumentAggregateFunction.Create<decimal, decimal, decimal>("ROW_NUMBER", () => 0m, (a, b) => a + b, value => value)); }
            else { builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("ROW_NUMBER", value => value)); }
        });
        Should.Throw<DatabaseParseException>(() => DocumentQueryRequest.FromOql(sql)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00006");
    }
}
