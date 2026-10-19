using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentExplainTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - EXPLAIN: scans, seeks and system collections use the chosen plan")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('fresh', 1)", "Insert")]
    [InlineData("INSERT INTO items (KEY k) SELECT META().id AS k FROM items", "InsertSelect")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('a', 1)", "Upsert")]
    [InlineData("UPSERT INTO items (KEY k, VALUE v) SELECT META().id AS k, score AS v FROM items", "UpsertSelect")]
    [InlineData("DELETE FROM items", "Delete")]
    [InlineData("DELETE FROM items WHERE score >= @minimum", "Delete")]
    [InlineData("UPDATE items SET score = score + 1", "Update")]
    [InlineData("UPDATE items SET score = score + 1 WHERE score >= @minimum", "Update")]
    [InlineData("SELECT name FROM items", "CollectionScan")]
    [InlineData("SELECT name FROM items WHERE score >= @minimum AND score < 10", "IndexSeek")]
    [InlineData("SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES", "SystemCollection")]
    public async Task Execute_Query_ShouldDescribeAccessAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("document-explain", new());
        var database = await engine.CreateDatabaseAsync("explain", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", "{\"name\":\"Ada\",\"score\":5}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        // Act
        await using var result = (await session.ExecuteAsync("EXPLAIN " + query,
            new Dictionary<string, object?> { ["minimum"] = 2 }, CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        await transaction.CommitAsync(CancellationToken.None);
        // Assert
        rows.ShouldContain(row => Equals(row[2], expected));
        if (expected is "Delete" or "Update")
        {
            string access = query.Contains("WHERE", StringComparison.Ordinal) ? "IndexSeek" : "CollectionScan";
            rows.Count.ShouldBe(2);
            rows[0][5].ShouldBe($"filter={access == "IndexSeek"}; access={access}");
            rows[1][1].ShouldBe(0);
            rows[1][2].ShouldBe(access);
            rows[1][4].ShouldBe(access == "IndexSeek" ? "by_score" : null);
        }
        if (expected == "IndexSeek")
        {
            rows[2][3].ShouldBe("items");
            rows[2][4].ShouldBe("by_score");
            rows[2][5].ShouldBe("score >= 2 AND score < 10");
        }
        var unchanged = (await collection.GetAsync(session, "a", CancellationToken.None)).ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetString(unchanged.Content.Span).ShouldBe("{\"name\":\"Ada\",\"score\":5}");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - EXPLAIN places DISTINCT above projection and grouping")]
    [InlineData("SELECT DISTINCT name FROM items", "Project", true)]
    [InlineData("SELECT VALUE name FROM items", "ProjectValue", false)]
    [InlineData("SELECT DISTINCT VALUE name FROM items", "ProjectValue", true)]
    [InlineData("SELECT DISTINCT name, COUNT(*) AS n FROM items GROUP BY name", "Group", true)]
    public async Task Execute_ProjectionStages_ShouldDescribeParentsAsync(string query, string stage, bool distinct)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("explain-projection", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        await using var description = (await session.ExecuteAsync("EXPLAIN " + query, cancellationToken: CancellationToken.None)).ShouldBeOfType<DatabasePlanDescription>();
        var nodes = new List<object?[]>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None))
        { nodes.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        // Assert
        nodes.Select(node => node[2]).ShouldBe(distinct ? new[] { "Select", "Distinct", stage, "CollectionScan" } : new[] { "Select", stage, "CollectionScan" });
        nodes[0][1].ShouldBeNull();
        for (int i = 1; i < nodes.Count; i++) { nodes[i][1].ShouldBe(nodes[i - 1][0]); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - EXPLAIN: every statement plan record includes collection DDL mappings")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only plan-family enumeration; shipped descriptions use a closed switch.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Test-only records exercise the mapping's type patterns without reading fields.")]
    public void Describe_EveryStatementPlan_ShouldHaveMapping()
    {
        // Arrange / Act
        var types = typeof(DocumentStatementPlan).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.Namespace == typeof(DocumentStatementPlan).Namespace &&
                type.Name.EndsWith("Plan", StringComparison.Ordinal)).ToArray();
        // Assert
        types.ShouldNotBeEmpty();
        foreach (var type in types)
        { DocumentPlanner.DescribeOperator(RuntimeHelpers.GetUninitializedObject(type)).ShouldNotBeNullOrEmpty(type.Name); }
        DocumentPlanner.DescribeOperator(new DocumentCreateCollectionPlan((OqlCreateCollectionExpression)
            ((OqlQueryStatement)new OqlQueryParser().Parse("CREATE COLLECTION c")).OqlExpression)).ShouldBe("CreateCollection");
        DocumentPlanner.DescribeOperator(new DocumentDropCollectionPlan((OqlDropCollectionExpression)
            ((OqlQueryStatement)new OqlQueryParser().Parse("DROP COLLECTION c")).OqlExpression)).ShouldBe("DropCollection");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - EXPLAIN: all collection and index DDL records have concrete descriptions")]
    [InlineData("CREATE COLLECTION fresh", "CreateCollection", "fresh")]
    [InlineData("DROP COLLECTION items", "DropCollection", "items")]
    [InlineData("CREATE INDEX by_score ON items (score)", "CreateIndex", "items")]
    [InlineData("DROP INDEX by_score ON items", "DropIndex", "items")]
    public async Task Describe_DdlPlan_ShouldMapTargetWithoutExecutingAsync(string statement, string expected, string target)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("document-explain-ddl", new());
        var database = await engine.CreateDatabaseAsync("ddl", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        var parsed = ((OqlQueryStatement)new OqlQueryParser().Parse(statement)).OqlExpression;
        // Act
        var plan = new DocumentPlanner(database.Catalog, transaction.Context.Snapshot, null).Plan(parsed);
        await using var description = DocumentPlanner.Describe(plan);
        // Assert
        await foreach (var row in description.GetRowsAsync(CancellationToken.None))
        {
            row.GetString(2).ShouldBe(expected);
            row.GetString(3).ShouldBe(target);
        }
        await transaction.CommitAsync(CancellationToken.None);
        var names = new List<string>();
        await foreach (var collection in session.GetCollectionsAsync(CancellationToken.None)) { names.Add(collection.Name); }
        names.ShouldBe(["items"]);
    }
}
