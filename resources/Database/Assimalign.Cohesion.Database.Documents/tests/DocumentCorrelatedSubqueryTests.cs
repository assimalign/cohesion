using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentCorrelatedSubqueryTests
{
    private readonly ITestOutputHelper _output;

    public DocumentCorrelatedSubqueryTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - Subquery work budget: positive configuration is frozen at engine creation")]
    public async Task Create_WorkBudget_ShouldValidateAndSnapshotOptionAsync()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => DocumentDatabaseEngine.Create("invalid-work", new() { StatementWorkBudget = 0 }))
            .ParamName.ShouldBe(nameof(DocumentDatabaseEngineOptions.StatementWorkBudget));
        var options = new DocumentDatabaseEngineOptions { StatementWorkBudget = 16 };
        await using var engine = DocumentDatabaseEngine.Create("frozen-work", options);
        options.StatementWorkBudget = 32;
        engine.EngineOptions.StatementWorkBudget.ShouldBe(16);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Correlation: generated predicates agree with brute force at every depth")]
    public async Task ExecuteAsync_GeneratedCorrelation_ShouldMatchBruteForceAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("generated-correlation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        int[] numbers = Enumerable.Range(0, 23).Select(index => (index * 17 + 11) % 31).ToArray();
        using var input = JsonDocument.Parse("[" + string.Join(",", numbers.Select(n => $"{{\"k\":\"id{n}\",\"n\":{n}}}")) + "]");
        await session.ExecuteAsync("INSERT INTO items (KEY k, VALUE v) SELECT i.k AS k, {n:i.n, tags:[i.n,i.n+1]} AS v FROM @numbers AS i",
            new Dictionary<string, object?> { ["numbers"] = input.RootElement }, CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT d.n, (SELECT RAW s.n FROM items s WHERE s.n <= d.n ORDER BY s.n) AS a FROM items d ORDER BY d.n");
        // Assert
        rows.Count.ShouldBe(numbers.Length);
        foreach (var row in rows)
        {
            int n = Convert.ToInt32(row.GetValue(0));
            ((JsonElement)row.GetValue(1)!).EnumerateArray().Select(item => item.GetInt32()).ShouldBe(numbers.Where(value => value <= n).Order());
        }
        session.LastQuerySubqueriesExecuted.ShouldBe(numbers.Length);
        var transitive = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT d.n, (SELECT RAW (SELECT RAW d.n FROM [1] AS v) FROM [1] AS s) FROM items d ORDER BY d.n");
        foreach (var row in transitive) { ((JsonElement)row.GetValue(1)!)[0][0].GetInt32().ShouldBe(Convert.ToInt32(row.GetValue(0))); }
        session.LastQuerySubqueriesExecuted.ShouldBe(numbers.Length * 2);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: FROM and expression scopes preserve local shadowing and outer values")]
    [InlineData("SELECT RAW (SELECT RAW i FROM d.tags AS i) FROM [{n:7,tags:[1,2]}] AS d", "[1,2]")]
    [InlineData("SELECT RAW (SELECT RAW i FROM d.tags) FROM [{tags:[1,2]}] AS d", "[null,null]")]
    [InlineData("SELECT RAW (SELECT RAW tags FROM d.tags) FROM [{tags:[1,2]}] AS d", "[1,2]")]
    [InlineData("SELECT RAW (SELECT RAW i.n FROM d AS i) FROM [{n:7}] AS d", "[7]")]
    [InlineData("SELECT RAW (SELECT RAW d.n FROM d) FROM [{n:7}] AS d", "[7]")]
    [InlineData("SELECT RAW (SELECT RAW n FROM [{n:2}] AS s) FROM [{n:7}] AS d", "[2]")]
    [InlineData("SELECT RAW (SELECT RAW d.n FROM [{n:2}] AS d) FROM [{n:7}] AS d", "[2]")]
    [InlineData("SELECT RAW (SELECT RAW x FROM [d.n] AS x) FROM [{n:7}] AS d", "[7]")]
    [InlineData("SELECT RAW (SELECT RAW x FROM (SELECT RAW d.n FROM [1] AS v) AS x) FROM [{n:7}] AS d", "[7]")]
    [InlineData("SELECT RAW (SELECT RAW u FROM [1] AS v) FROM [{tags:[7]}] AS d UNNEST d.tags AS u", "[7]")]
    [InlineData("SELECT RAW ARRAY (SELECT RAW x FROM [1] AS v) FOR x IN [7] END FROM [1] AS d", "[[7]]")]
    [InlineData("SELECT RAW (SELECT RAW v FROM d.tags AS v) FROM [{tags:null},{}] AS d LIMIT 1", "[null]")]
    [InlineData("SELECT RAW (SELECT RAW v FROM d.tags AS v) FROM [{}] AS d", "[]")]
    [InlineData("SELECT RAW (SELECT RAW d.tags FROM [1] AS v) FROM [{}] AS d", "[null]")]
    [InlineData("SELECT RAW (SELECT * FROM [1] AS v) FROM [{n:7}] AS d", "[{\"v\":1}]")]
    public async Task ExecuteAsync_CorrelatedScopes_ShouldPreserveExpectedArrayAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("scoped-correlation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        ((JsonElement)rows.Single().GetValue(0)!).GetRawText().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Correlation: ORDER BY nested reads bind projection aliases")]
    public async Task ExecuteAsync_ProjectionAlias_ShouldOrderPerRowAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ordered-correlation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT -d.n AS p FROM items d ORDER BY (SELECT RAW p FROM [1] AS v)");
        // Assert
        rows.Select(row => row.GetValue(0)).ShouldBe(new object?[] { -3m, -2m, -1m });
        var plan = await DocumentSubqueryTests.RowsAsync(session,
            "EXPLAIN SELECT (SELECT RAW d.n FROM [1] AS v) FROM items d");
        plan.Single(row => row.GetString(2) == "SubqueryExpression").GetString(5)!.ShouldContain("correlation=d", Case.Sensitive);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Correlation: filter-only nested arrays release each row's memory")]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task ExecuteAsync_FilterOnly_ShouldKeepNestedRetentionFlatAsync(int count)
    {
        // Arrange
        var builder = DocumentDatabaseEngine.CreateBuilder("owned-correlation");
        builder.Options.StatementMemoryBudget = 16 * 1024 * 1024;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("payload", value => new string('x', 8192)));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var input = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, count).Select(n => $"{{\"k\":\"id{n}\",\"n\":{n}}}")) + "]");
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY k, VALUE v) SELECT n.k AS k, {n:n.n} AS v FROM @input AS n",
            new Dictionary<string, object?> { ["input"] = input.RootElement }, CancellationToken.None);
        var request = DocumentQueryRequest.FromOql("SELECT RAW 1 FROM items d WHERE NOT EXISTS (SELECT RAW payload(d.n) FROM [1] AS v)");
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            long first = 0, last = 0;
            memory.RetentionObserved = () =>
            {
                if (session.LastQuerySubqueriesExecuted == 1) { first = Math.Max(first, memory.Bytes); }
                if (session.LastQuerySubqueriesExecuted == count) { last = Math.Max(last, memory.Bytes); }
            };
            await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None))
            { await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { throw new InvalidOperationException("No row should survive NOT EXISTS."); } }
            session.LastQuerySubqueriesExecuted.ShouldBe(count);
            last.ShouldBe(first);
            memory.Bytes.ShouldBe(0);
            _output.WriteLine($"Outer rows={count}; first/last retained peak={first}/{last} B; final={memory.Bytes} B.");
            return true;
        }, CancellationToken.None);
    }
}
