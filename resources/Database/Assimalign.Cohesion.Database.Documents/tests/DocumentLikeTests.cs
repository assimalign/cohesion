using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentLikeTests
{
    private readonly ITestOutputHelper _output;

    public DocumentLikeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - LIKE: long literal suffix observes statement cancellation within 500 ms")]
    [InlineData("SELECT @input LIKE @pattern FROM items")]
    [InlineData("SELECT 1 FROM items WHERE @input LIKE @pattern")]
    [InlineData("DELETE FROM items WHERE @input LIKE @pattern")]
    [InlineData("SELECT COUNT(*) FROM items HAVING @input LIKE @pattern")]
    [InlineData("SELECT 1 FROM items ORDER BY @input LIKE @pattern")]
    [InlineData("SELECT COUNT(*) FROM items GROUP BY @input LIKE @pattern")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('two', {'match':@input LIKE @pattern})")]
    [InlineData("EXPLAIN SELECT 1 FROM items WHERE score = CASE WHEN @input LIKE @pattern THEN 1 ELSE 0 END")]
    public async Task Execute_LongLikeSuffix_ShouldCancelWithinHalfSecondAsync(string statement)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("like-cancel", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", Encoding.UTF8.GetBytes("{}"), cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?>
        {
            ["input"] = new string('a', 50_000),
            ["pattern"] = "%" + new string('a', 25_000) + "b",
        };
        int cancellations = 0;
        var total = Stopwatch.StartNew();
        // Act / Assert: run for a minimum duration and require the cancellation to occur repeatedly.
        do
        {
            using var source = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();
            source.CancelAfter(50);
            var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                var result = await session.ExecuteAsync(statement, parameters, source.Token);
                if (result is DatabaseResultSet rows) { await rows.DisposeAsync(); }
            });
            watch.Stop();
            source.IsCancellationRequested.ShouldBeTrue();
            error.CancellationToken.ShouldBe(source.Token);
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
            cancellations++;
            _output.WriteLine($"Cancelled probe {cancellations}: {watch.Elapsed.TotalMilliseconds:F1} ms");
        }
        while ((total.Elapsed < TimeSpan.FromMilliseconds(150) || cancellations < 3) && total.Elapsed < TimeSpan.FromSeconds(3));
        cancellations.ShouldBeGreaterThanOrEqualTo(3);
        // A cancelled automatic statement must release its session for later work.
        await using var next = (DatabaseResultSet)await session.ExecuteAsync("SELECT 1 FROM items", cancellationToken: CancellationToken.None);
        next.Status.ShouldBe(DatabaseResultStatus.Success);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - LIKE: constant pattern compilation has bounded allocation across rows")]
    [InlineData("literal", " ESCAPE '!'")]
    [InlineData("@pattern", "")]
    [InlineData("@pattern", " ESCAPE @escape")]
    [InlineData("CASE WHEN 1 THEN @pattern ELSE '' END", " ESCAPE @escape")]
    public void Evaluate_ConstantLikePattern_ShouldReuseCompilationAcrossRows(string pattern, string escape)
    {
        // Arrange
        var parameters = new Dictionary<string, object?> { ["input"] = "z", ["pattern"] = new string('a', 25_000), ["escape"] = "!" };
        if (pattern == "literal") { pattern = "'" + new string('a', 25_000) + "'"; }
        var select = (OqlSelectExpression)DocumentQueryRequest.FromOql($"SELECT @input LIKE {pattern}{escape} FROM items").Statement.OqlExpression;
        var expression = select.Projections[0].Expression;
        var evaluator = new DocumentExpressionEvaluator(null, parameters, CancellationToken.None);
        evaluator.Evaluate(expression, default).ShouldBe(false);
        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        int matches = 0;
        for (int i = 0; i < 64; i++) { if (evaluator.Evaluate(expression, default) is true) { matches++; } }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert: recompiling 25,000 tokens for every row allocates over 10 MiB.
        matches.ShouldBe(0);
        allocated.ShouldBeLessThan(256_000);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - LIKE: dynamic patterns and escapes vary by row and constants reset by statement")]
    public async Task Execute_VaryingLikeInputs_ShouldKeepPatternsLocalAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("like-patterns", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("""{"input":"x%","pattern":"x!%","escape":"!"}"""), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "b", Encoding.UTF8.GetBytes("""{"input":"x%","pattern":"x!%","escape":""}"""), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "c", Encoding.UTF8.GetBytes("""{"input":"xyz","pattern":"x%","escape":"!"}"""), cancellationToken: CancellationToken.None);
        // Act / Assert
        await using var dynamic = (DatabaseResultSet)await session.ExecuteAsync("SELECT d.input LIKE d.pattern ESCAPE d.escape FROM items d", cancellationToken: CancellationToken.None);
        var values = new List<bool>();
        await foreach (var row in dynamic.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetBoolean(0)); }
        values.ShouldBe([true, false, true]);
        foreach (string pattern in new[] { "x%", "z%" })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT 'xyz' LIKE @pattern FROM items",
                new Dictionary<string, object?> { ["pattern"] = pattern }, CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetBoolean(0).ShouldBe(pattern == "x%"); }
        }
    }
}
